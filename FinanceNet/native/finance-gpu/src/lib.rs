//! finance_gpu — Vulkan compute (through wgpu) for Finance.GPU, exposed through a C ABI.
//!
//! The kernels in `kernels.wgsl` are ports of Finance.GPU's ILGPU kernels with the same counter-based RNG, so a
//! seed gives the same random numbers on every backend. They need 64-bit floats and integers in shaders
//! (`SHADER_F64` + `SHADER_INT64`), which wgpu exposes on Vulkan; devices without them are listed as unusable.
//! (wgpu's Direct3D 12 and Metal backends have no FP64 shaders; Finance.GPU drives Direct3D 12 itself.)
//!
//! Conventions: status codes (0 = ok, negative = error), caller-allocated buffers, panics caught at the boundary,
//! `fgpu_last_error` explains the most recent failure on the calling thread.
//!
//! Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;

pub const FGPU_OK: i32 = 0;
pub const FGPU_ERR_NULL: i32 = -1;
pub const FGPU_ERR_ARG: i32 = -2;
pub const FGPU_ERR_PANIC: i32 = -3;
pub const FGPU_ERR_DEVICE: i32 = -4;

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_error(e: impl Into<String>) {
    LAST_ERROR.with(|l| *l.borrow_mut() = e.into());
}

fn guard<F: FnOnce() -> i32>(f: F) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(v) => v,
        Err(p) => {
            let msg = p.downcast_ref::<String>().cloned().or_else(|| p.downcast_ref::<&str>().map(|s| s.to_string())).unwrap_or_default();
            set_error(format!("panic: {msg}"));
            FGPU_ERR_PANIC
        }
    }
}

const REQUIRED: wgpu::Features = wgpu::Features::SHADER_F64.union(wgpu::Features::SHADER_INT64);

fn instance() -> wgpu::Instance {
    wgpu::Instance::new(&wgpu::InstanceDescriptor { backends: wgpu::Backends::VULKAN, ..Default::default() })
}

fn adapters() -> Vec<wgpu::Adapter> {
    instance().enumerate_adapters(wgpu::Backends::VULKAN)
}

/// A Vulkan device with the compiled kernels.
pub struct GpuDevice {
    device: wgpu::Device,
    queue: wgpu::Queue,
    layout: wgpu::BindGroupLayout,
    pipelines: [wgpu::ComputePipeline; 4],
    pub name: String,
    max_buffer: u64,
}

#[derive(Clone, Copy)]
pub enum Kernel {
    European = 0,
    Asian = 1,
    BlackScholes = 2,
    PortfolioReturn = 3,
}

impl GpuDevice {
    /// Opens Vulkan adapter `index` (in enumeration order).
    pub fn open(index: usize) -> Result<GpuDevice, String> {
        let list = adapters();
        let adapter = list.into_iter().nth(index).ok_or_else(|| format!("no Vulkan adapter #{index}"))?;
        let info = adapter.get_info();
        if !adapter.features().contains(REQUIRED) {
            return Err(format!("{}: no 64-bit float/integer shader support (shaderFloat64/shaderInt64)", info.name));
        }
        let limits = adapter.limits();
        let (device, queue) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            label: Some("finance-gpu"),
            required_features: REQUIRED,
            required_limits: limits.clone(),
            ..Default::default()
        }, None))
        .map_err(|e| format!("{}: {e}", info.name))?;
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("finance kernels"),
            source: wgpu::ShaderSource::Wgsl(include_str!("kernels.wgsl").into()),
        });
        let entry = |binding: u32, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer { ty: wgpu::BufferBindingType::Storage { read_only }, has_dynamic_offset: false, min_binding_size: None },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: None,
            entries: &[entry(0, true), entry(1, true), entry(2, true), entry(3, false)],
        });
        let pl = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor { label: None, bind_group_layouts: &[&layout], push_constant_ranges: &[] });
        let make = |name: &str| {
            device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some(name),
                layout: Some(&pl),
                module: &module,
                entry_point: Some(name),
                compilation_options: Default::default(),
                cache: None,
            })
        };
        let pipelines = [make("mc_european"), make("mc_asian"), make("black_scholes"), make("portfolio_return")];
        let max_buffer = (limits.max_storage_buffer_binding_size as u64).min(limits.max_buffer_size);
        Ok(GpuDevice { device, queue, layout, pipelines, name: info.name, max_buffer })
    }

    /// Runs `kernel` over `threads` invocations and returns `out_len` doubles from binding 3.
    pub fn run(&self, kernel: Kernel, fp: &[f64], ip: &[u32], input: &[f64], out_len: usize, threads: u32) -> Result<Vec<f64>, String> {
        use wgpu::util::DeviceExt;
        let bytes = |v: &[f64]| -> Vec<u8> { v.iter().flat_map(|x| x.to_le_bytes()).collect() };
        let ibytes = |v: &[u32]| -> Vec<u8> { v.iter().flat_map(|x| x.to_le_bytes()).collect() };
        let pad = |mut b: Vec<u8>| {
            if b.is_empty() {
                b.resize(8, 0);
            }
            b
        };
        let in_bytes = pad(bytes(input));
        let out_size = (out_len.max(1) * 8) as u64;
        if in_bytes.len() as u64 > self.max_buffer || out_size > self.max_buffer {
            return Err(format!("buffer larger than the device limit ({} bytes)", self.max_buffer));
        }
        let usage = wgpu::BufferUsages::STORAGE;
        let b0 = self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor { label: None, contents: &pad(bytes(fp)), usage });
        let b1 = self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor { label: None, contents: &pad(ibytes(ip)), usage });
        let b2 = self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor { label: None, contents: &in_bytes, usage });
        let b3 = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: out_size,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let staging = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: out_size,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let group = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: None,
            layout: &self.layout,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: b0.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: b1.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: b2.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 3, resource: b3.as_entire_binding() },
            ],
        });
        // 64 threads per group; more than 65535 groups spill into a second grid dimension.
        let groups = threads.div_ceil(64).max(1);
        let (gx, gy) = if groups <= 65535 { (groups, 1) } else { (65535, groups.div_ceil(65535)) };
        let mut enc = self.device.create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
        {
            let mut pass = enc.begin_compute_pass(&wgpu::ComputePassDescriptor::default());
            pass.set_pipeline(&self.pipelines[kernel as usize]);
            pass.set_bind_group(0, &group, &[]);
            pass.dispatch_workgroups(gx, gy, 1);
        }
        enc.copy_buffer_to_buffer(&b3, 0, &staging, 0, out_size);
        self.queue.submit([enc.finish()]);
        let slice = staging.slice(..);
        let (tx, rx) = std::sync::mpsc::channel();
        slice.map_async(wgpu::MapMode::Read, move |r| {
            let _ = tx.send(r);
        });
        let _ = self.device.poll(wgpu::Maintain::Wait);
        rx.recv().map_err(|e| e.to_string())?.map_err(|e| e.to_string())?;
        let data = slice.get_mapped_range();
        let out = data.chunks_exact(8).take(out_len).map(|c| f64::from_le_bytes(c.try_into().unwrap())).collect();
        drop(data);
        staging.unmap();
        Ok(out)
    }
}

// ---------------------------------------------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------------------------------------------

/// Version packed as major * 10000 + minor * 100 + patch.
#[no_mangle]
pub extern "C" fn fgpu_version() -> u32 {
    let major: u32 = env!("CARGO_PKG_VERSION_MAJOR").parse().unwrap_or(0);
    let minor: u32 = env!("CARGO_PKG_VERSION_MINOR").parse().unwrap_or(0);
    let patch: u32 = env!("CARGO_PKG_VERSION_PATCH").parse().unwrap_or(0);
    major * 10000 + minor * 100 + patch
}

/// Copies the last error message of this thread into `buf` (UTF-8, not terminated); returns its byte length.
#[no_mangle]
pub unsafe extern "C" fn fgpu_last_error(buf: *mut u8, cap: usize) -> i32 {
    LAST_ERROR.with(|l| {
        let s = l.borrow();
        if !buf.is_null() && cap >= s.len() {
            slice::from_raw_parts_mut(buf, s.len()).copy_from_slice(s.as_bytes());
        }
        s.len() as i32
    })
}

/// Describes the Vulkan adapters, one per line: `name|device type|f64 (0/1)|i64 (0/1)|driver`. Returns the byte
/// length needed (call with `cap = 0` to size the buffer).
#[no_mangle]
pub unsafe extern "C" fn fgpu_list_devices(buf: *mut u8, cap: usize) -> i32 {
    guard(|| {
        let text = adapters()
            .iter()
            .map(|a| {
                let i = a.get_info();
                let f = a.features();
                format!(
                    "{}|{:?}|{}|{}|{} {}",
                    i.name.replace(['|', '\n'], " "),
                    i.device_type,
                    f.contains(wgpu::Features::SHADER_F64) as u8,
                    f.contains(wgpu::Features::SHADER_INT64) as u8,
                    i.driver.replace(['|', '\n'], " "),
                    i.driver_info.replace(['|', '\n'], " ")
                )
            })
            .collect::<Vec<_>>()
            .join("\n");
        if !buf.is_null() && cap >= text.len() {
            slice::from_raw_parts_mut(buf, text.len()).copy_from_slice(text.as_bytes());
        }
        text.len() as i32
    })
}

/// Opens Vulkan adapter `index`; returns null on failure (see `fgpu_last_error`).
#[no_mangle]
pub extern "C" fn fgpu_open(index: u32) -> *mut GpuDevice {
    match catch_unwind(|| GpuDevice::open(index as usize)) {
        Ok(Ok(d)) => Box::into_raw(Box::new(d)),
        Ok(Err(e)) => {
            set_error(e);
            std::ptr::null_mut()
        }
        Err(_) => {
            set_error("panic while opening the device");
            std::ptr::null_mut()
        }
    }
}

/// Releases a device returned by `fgpu_open`.
#[no_mangle]
pub unsafe extern "C" fn fgpu_close(device: *mut GpuDevice) {
    if !device.is_null() {
        drop(Box::from_raw(device));
    }
}

/// Copies the device name into `buf`; returns its byte length.
#[no_mangle]
pub unsafe extern "C" fn fgpu_device_name(device: *const GpuDevice, buf: *mut u8, cap: usize) -> i32 {
    if device.is_null() {
        return FGPU_ERR_NULL;
    }
    let name = &(*device).name;
    if !buf.is_null() && cap >= name.len() {
        slice::from_raw_parts_mut(buf, name.len()).copy_from_slice(name.as_bytes());
    }
    name.len() as i32
}

fn finish(result: Result<Vec<f64>, String>, out: &mut [f64]) -> i32 {
    match result {
        Ok(v) => {
            out.copy_from_slice(&v[..out.len()]);
            FGPU_OK
        }
        Err(e) => {
            set_error(e);
            FGPU_ERR_DEVICE
        }
    }
}

/// Monte Carlo European (`asian = 0`) or arithmetic Asian (`asian = 1`) option: per-thread payoff sums and sums of
/// squares (`sums`, `sumsq`, each `threads` long), exactly as Finance.GPU's ILGPU kernels produce them.
#[no_mangle]
pub unsafe extern "C" fn fgpu_monte_carlo(
    device: *const GpuDevice, asian: u32, spot: f64, strike: f64, time: f64, rate: f64, dividend: f64, vol: f64, is_call: u32,
    steps: u32, paths_per_thread: u64, total_paths: u64, seed: u64, threads: u32, sums: *mut f64, sumsq: *mut f64,
) -> i32 {
    guard(|| {
        if device.is_null() || sums.is_null() || sumsq.is_null() {
            return FGPU_ERR_NULL;
        }
        if threads == 0 || paths_per_thread == 0 || (asian != 0 && steps == 0) {
            return FGPU_ERR_ARG;
        }
        let fp = [spot, strike, time, rate, dividend, vol];
        let ip = [is_call, steps, paths_per_thread as u32, (paths_per_thread >> 32) as u32, total_paths as u32, (total_paths >> 32) as u32,
            seed as u32, (seed >> 32) as u32, threads];
        let kernel = if asian != 0 { Kernel::Asian } else { Kernel::European };
        match (*device).run(kernel, &fp, &ip, &[], 2 * threads as usize, threads) {
            Ok(v) => {
                let t = threads as usize;
                slice::from_raw_parts_mut(sums, t).copy_from_slice(&v[..t]);
                slice::from_raw_parts_mut(sumsq, t).copy_from_slice(&v[t..2 * t]);
                FGPU_OK
            }
            Err(e) => {
                set_error(e);
                FGPU_ERR_DEVICE
            }
        }
    })
}

/// Batch Black-Scholes: `input` = 7 planes of `n` (spot, strike, time, rate, dividend, vol, isCall 0/1); `output` =
/// 6 planes (price, delta, gamma, vega, theta, rho).
#[no_mangle]
pub unsafe extern "C" fn fgpu_black_scholes(device: *const GpuDevice, input: *const f64, n: u32, output: *mut f64) -> i32 {
    guard(|| {
        if device.is_null() || input.is_null() || output.is_null() {
            return FGPU_ERR_NULL;
        }
        let n_us = n as usize;
        let inp = slice::from_raw_parts(input, 7 * n_us);
        let out = slice::from_raw_parts_mut(output, 6 * n_us);
        finish((*device).run(Kernel::BlackScholes, &[], &[n], inp, 6 * n_us, n), out)
    })
}

/// Monte Carlo portfolio returns: `chol` (assets²), `weights`, `drifts` (assets each); `returns` gets `scenarios`.
#[no_mangle]
pub unsafe extern "C" fn fgpu_portfolio_returns(
    device: *const GpuDevice, chol: *const f64, weights: *const f64, drifts: *const f64, assets: u32, sqrt_horizon: f64, seed: u64,
    scenarios: u32, returns: *mut f64,
) -> i32 {
    guard(|| {
        if device.is_null() || chol.is_null() || weights.is_null() || drifts.is_null() || returns.is_null() {
            return FGPU_ERR_NULL;
        }
        let n = assets as usize;
        let mut inp = Vec::with_capacity(n * n + 2 * n);
        inp.extend_from_slice(slice::from_raw_parts(chol, n * n));
        inp.extend_from_slice(slice::from_raw_parts(weights, n));
        inp.extend_from_slice(slice::from_raw_parts(drifts, n));
        let out = slice::from_raw_parts_mut(returns, scenarios as usize);
        let ip = [assets, scenarios, seed as u32, (seed >> 32) as u32];
        finish((*device).run(Kernel::PortfolioReturn, &[sqrt_horizon], &ip, &inp, scenarios as usize, scenarios), out)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Host copy of GpuMath (SplitMix64 counter RNG, Box-Muller) for comparisons.
    fn mix(mut z: u64) -> u64 {
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
        z ^ (z >> 31)
    }

    fn uniform(seed: u64, c: u64) -> f64 {
        (((mix(seed.wrapping_add((c + 1).wrapping_mul(0x9E3779B97F4A7C15))) >> 11) + 1) as f64) * (1.0 / 9007199254740992.0)
    }

    fn gaussian(seed: u64, c: u64) -> f64 {
        let (u1, u2) = (uniform(seed, 2 * c), uniform(seed, 2 * c + 1));
        (-2.0 * u1.ln()).sqrt() * (std::f64::consts::TAU * u2).cos()
    }

    fn device() -> Option<GpuDevice> {
        let list = adapters();
        let i = list.iter().position(|a| a.features().contains(REQUIRED))?;
        GpuDevice::open(i).ok()
    }

    #[test]
    fn european_sums_match_the_host_rng() {
        let Some(d) = device() else {
            eprintln!("no Vulkan FP64 device - skipped");
            return;
        };
        let (threads, ppt, total, seed) = (256u32, 40u64, 10_000u64, 42u64);
        let ip = [1, 1, ppt as u32, 0, total as u32, 0, seed as u32, 0, threads];
        let fp = [100.0, 100.0, 1.0, 0.05, 0.0, 0.2];
        let v = d.run(Kernel::European, &fp, &ip, &[], 2 * threads as usize, threads).unwrap();
        let drift = (0.05 - 0.5 * 0.04) * 1.0;
        let diff = 0.2f64;
        for t in [0usize, 17, 255] {
            let mut s1 = 0.0;
            for p in (t as u64 * ppt)..((t as u64 + 1) * ppt).min(total) {
                let z = gaussian(seed, p);
                let up = 100.0 * (drift + diff * z).exp();
                let dn = 100.0 * (drift - diff * z).exp();
                s1 += 0.5 * ((up - 100.0f64).max(0.0) + (dn - 100.0f64).max(0.0));
            }
            assert!((v[t] - s1).abs() <= 1e-10 * s1.abs().max(1.0), "thread {t}: gpu {} host {}", v[t], s1);
        }
    }

    #[test]
    fn black_scholes_matches_closed_form() {
        let Some(d) = device() else { return };
        // ATM call, S=K=100, T=1, r=5%, q=0, vol=20%: price 10.450583572185565 (Hull).
        let inp = [100.0, 100.0, 1.0, 0.05, 0.0, 0.2, 1.0];
        let v = d.run(Kernel::BlackScholes, &[], &[1], &inp, 6, 1).unwrap();
        assert!((v[0] - 10.450583572185565).abs() < 1e-12, "{}", v[0]);
        assert!((v[1] - 0.6368306511756191).abs() < 1e-12, "{}", v[1]);
    }
}
