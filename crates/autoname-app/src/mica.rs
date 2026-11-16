//! Real-time Windows 11 Mica material backdrop shader.
//! Samples `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper` once at startup via `gdiplus.dll`,
//! boosts saturation, applies multi-pass Gaussian-like blur, and blends dynamically with screen coordinates.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::path::PathBuf;
use std::ptr::null_mut;

pub const GRID_W: usize = 160;
pub const GRID_H: usize = 90;

#[derive(Clone, Copy, Debug)]
pub struct MicaPixel {
    pub r: f32,
    pub g: f32,
    pub b: f32,
}

pub struct MicaBackdrop {
    pub grid: Vec<MicaPixel>,
    pub from_wallpaper: bool,
}

impl MicaBackdrop {
    pub fn load() -> Self {
        if let Some(grid) = load_wallpaper_grid() {
            let mut bd = Self {
                grid,
                from_wallpaper: true,
            };
            bd.boost_saturation(1.35);
            bd.blur(9, 4);
            return bd;
        }
        let mut bd = Self {
            grid: fallback_aurora_grid(),
            from_wallpaper: false,
        };
        bd.blur(6, 2);
        bd
    }

    fn boost_saturation(&mut self, factor: f32) {
        for px in &mut self.grid {
            let lum = 0.299 * px.r + 0.587 * px.g + 0.114 * px.b;
            px.r = (lum + (px.r - lum) * factor).clamp(0.0, 255.0);
            px.g = (lum + (px.g - lum) * factor).clamp(0.0, 255.0);
            px.b = (lum + (px.b - lum) * factor).clamp(0.0, 255.0);
        }
    }

    fn blur(&mut self, radius: usize, passes: usize) {
        let mut tmp = vec![
            MicaPixel {
                r: 0.0,
                g: 0.0,
                b: 0.0
            };
            GRID_W * GRID_H
        ];
        for _ in 0..passes {
            // Horizontal pass (clamp edges)
            for y in 0..GRID_H {
                let row = y * GRID_W;
                for x in 0..GRID_W {
                    let mut sr = 0.0f32;
                    let mut sg = 0.0f32;
                    let mut sb = 0.0f32;
                    let mut cnt = 0.0f32;
                    let x0 = x.saturating_sub(radius);
                    let x1 = (x + radius).min(GRID_W - 1);
                    for kx in x0..=x1 {
                        let p = self.grid[row + kx];
                        sr += p.r;
                        sg += p.g;
                        sb += p.b;
                        cnt += 1.0;
                    }
                    tmp[row + x] = MicaPixel {
                        r: sr / cnt,
                        g: sg / cnt,
                        b: sb / cnt,
                    };
                }
            }
            // Vertical pass (clamp edges)
            for y in 0..GRID_H {
                let y0 = y.saturating_sub(radius);
                let y1 = (y + radius).min(GRID_H - 1);
                let cnt = (y1 - y0 + 1) as f32;
                for x in 0..GRID_W {
                    let mut sr = 0.0f32;
                    let mut sg = 0.0f32;
                    let mut sb = 0.0f32;
                    for ky in y0..=y1 {
                        let p = tmp[ky * GRID_W + x];
                        sr += p.r;
                        sg += p.g;
                        sb += p.b;
                    }
                    self.grid[y * GRID_W + x] = MicaPixel {
                        r: sr / cnt,
                        g: sg / cnt,
                        b: sb / cnt,
                    };
                }
            }
        }
    }

    #[inline]
    pub fn sample_uv(&self, u: f32, v: f32) -> (f32, f32, f32) {
        let fx = u.clamp(0.0, 1.0) * (GRID_W - 1) as f32;
        let fy = v.clamp(0.0, 1.0) * (GRID_H - 1) as f32;
        let x0 = (fx.floor() as usize).min(GRID_W - 1);
        let y0 = (fy.floor() as usize).min(GRID_H - 1);
        let x1 = (x0 + 1).min(GRID_W - 1);
        let y1 = (y0 + 1).min(GRID_H - 1);
        let tx = fx - x0 as f32;
        let ty = fy - y0 as f32;

        let p00 = self.grid[y0 * GRID_W + x0];
        let p10 = self.grid[y0 * GRID_W + x1];
        let p01 = self.grid[y1 * GRID_W + x0];
        let p11 = self.grid[y1 * GRID_W + x1];

        let r0 = p00.r + (p10.r - p00.r) * tx;
        let r1 = p01.r + (p11.r - p01.r) * tx;
        let g0 = p00.g + (p10.g - p00.g) * tx;
        let g1 = p01.g + (p11.g - p01.g) * tx;
        let b0 = p00.b + (p10.b - p00.b) * tx;
        let b1 = p01.b + (p11.b - p01.b) * tx;

        (
            r0 + (r1 - r0) * ty,
            g0 + (g1 - g0) * ty,
            b0 + (b1 - b0) * ty,
        )
    }

    /// Paint the full Mica base buffer for window `(win_x, win_y)` on screen `(screen_w, screen_h)`.
    pub fn paint_base(
        &self,
        pixels: &mut [u32],
        w: i32,
        h: i32,
        win_x: i32,
        win_y: i32,
        screen_w: i32,
        screen_h: i32,
        dark_mode: bool,
    ) {
        let sw = screen_w.max(1) as f32;
        let sh = screen_h.max(1) as f32;

        let (tint_r, tint_g, tint_b, tint_w, wall_w) = if dark_mode {
            (18.0f32, 20.0f32, 28.0f32, 0.66f32, 0.34f32)
        } else {
            (242.0f32, 245.0f32, 250.0f32, 0.74f32, 0.26f32)
        };

        for py in 0..h {
            let sy = (win_y + py) as f32 / sh;
            let row = (py * w) as usize;
            for px in 0..w {
                let sx = (win_x + px) as f32 / sw;
                let (wr, wg, wb) = self.sample_uv(sx, sy);

                // Subtle +/-1 spatial dither to eliminate 8-bit banding (Win11 noise recipe)
                let hash = ((px as u32).wrapping_mul(73_856_093)
                    ^ (py as u32).wrapping_mul(19_349_663))
                    & 3;
                let dither = match hash {
                    0 => -1.0f32,
                    1 => 1.0f32,
                    _ => 0.0f32,
                };

                let r = (tint_r * tint_w + wr * wall_w + dither).clamp(0.0, 255.0) as u32;
                let g = (tint_g * tint_w + wg * wall_w + dither).clamp(0.0, 255.0) as u32;
                let b = (tint_b * tint_w + wb * wall_w + dither).clamp(0.0, 255.0) as u32;

                pixels[row + px as usize] = b | (g << 8) | (r << 16) | 0xFF00_0000;
            }
        }
    }
}

fn fallback_aurora_grid() -> Vec<MicaPixel> {
    let mut grid = Vec::with_capacity(GRID_W * GRID_H);
    for y in 0..GRID_H {
        let v = y as f32 / (GRID_H - 1) as f32;
        for x in 0..GRID_W {
            let u = x as f32 / (GRID_W - 1) as f32;
            let glow = (1.0 - ((u - 0.52).powi(2) + (v - 0.38).powi(2)).sqrt() * 1.4).max(0.0);
            let r = 34.0 + glow * 62.0 + u * 24.0;
            let g = 92.0 + glow * 135.0 + (1.0 - v) * 40.0;
            let b = 158.0 + glow * 88.0 + v * 35.0;
            grid.push(MicaPixel { r, g, b });
        }
    }
    grid
}

#[cfg(windows)]
fn load_wallpaper_grid() -> Option<Vec<MicaPixel>> {
    use std::ffi::c_void;
    use windows_sys::Win32::Foundation::HMODULE;
    use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

    let appdata = std::env::var_os("APPDATA")?;
    let wp_path: PathBuf = PathBuf::from(appdata)
        .join("Microsoft")
        .join("Windows")
        .join("Themes")
        .join("TranscodedWallpaper");
    if !wp_path.is_file() {
        return None;
    }

    #[repr(C)]
    struct GdiplusStartupInput {
        gdiplus_version: u32,
        debug_event_callback: usize,
        suppress_background_thread: i32,
        suppress_external_codecs: i32,
    }

    type FnStartup = unsafe extern "system" fn(*mut usize, *const GdiplusStartupInput, *mut c_void) -> i32;
    type FnShutdown = unsafe extern "system" fn(usize);
    type FnLoadImage = unsafe extern "system" fn(*const u16, *mut *mut c_void) -> i32;
    type FnGetThumb =
        unsafe extern "system" fn(*mut c_void, u32, u32, *mut *mut c_void, usize, usize) -> i32;
    type FnGetPixel = unsafe extern "system" fn(*mut c_void, u32, u32, *mut u32) -> i32;
    type FnDispose = unsafe extern "system" fn(*mut c_void) -> i32;

    let dll_name: Vec<u16> = OsStr::new("gdiplus.dll").encode_wide().chain(Some(0)).collect();
    let wp_wide: Vec<u16> = wp_path.as_os_str().encode_wide().chain(Some(0)).collect();

    unsafe {
        let hmod: HMODULE = LoadLibraryW(dll_name.as_ptr());
        if hmod.is_null() {
            return None;
        }
        let startup: FnStartup =
            std::mem::transmute(GetProcAddress(hmod, b"GdiplusStartup\0".as_ptr())?);
        let shutdown: FnShutdown =
            std::mem::transmute(GetProcAddress(hmod, b"GdiplusShutdown\0".as_ptr())?);
        let load_img: FnLoadImage =
            std::mem::transmute(GetProcAddress(hmod, b"GdipLoadImageFromFile\0".as_ptr())?);
        let get_thumb: FnGetThumb =
            std::mem::transmute(GetProcAddress(hmod, b"GdipGetImageThumbnail\0".as_ptr())?);
        let get_pixel: FnGetPixel =
            std::mem::transmute(GetProcAddress(hmod, b"GdipBitmapGetPixel\0".as_ptr())?);
        let dispose: FnDispose =
            std::mem::transmute(GetProcAddress(hmod, b"GdipDisposeImage\0".as_ptr())?);

        let mut token: usize = 0;
        let input = GdiplusStartupInput {
            gdiplus_version: 1,
            debug_event_callback: 0,
            suppress_background_thread: 0,
            suppress_external_codecs: 0,
        };
        if startup(&mut token, &input, null_mut()) != 0 {
            return None;
        }

        let mut img: *mut c_void = null_mut();
        if load_img(wp_wide.as_ptr(), &mut img) != 0 || img.is_null() {
            shutdown(token);
            return None;
        }

        let mut thumb: *mut c_void = null_mut();
        let thumb_status = get_thumb(img, GRID_W as u32, GRID_H as u32, &mut thumb, 0, 0);
        dispose(img);

        if thumb_status != 0 || thumb.is_null() {
            shutdown(token);
            return None;
        }

        let mut grid = Vec::with_capacity(GRID_W * GRID_H);
        for y in 0..GRID_H {
            for x in 0..GRID_W {
                let mut argb: u32 = 0;
                let _ = get_pixel(thumb, x as u32, y as u32, &mut argb);
                grid.push(MicaPixel {
                    r: ((argb >> 16) & 0xFF) as f32,
                    g: ((argb >> 8) & 0xFF) as f32,
                    b: (argb & 0xFF) as f32,
                });
            }
        }

        dispose(thumb);
        shutdown(token);
        Some(grid)
    }
}

#[cfg(not(windows))]
fn load_wallpaper_grid() -> Option<Vec<MicaPixel>> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mica_backdrop_loads_and_paints() {
        let bd = MicaBackdrop::load();
        assert_eq!(bd.grid.len(), GRID_W * GRID_H);
        let (r, g, b) = bd.sample_uv(0.5, 0.5);
        assert!(r >= 0.0 && g >= 0.0 && b >= 0.0);

        let mut buf = vec![0u32; 64 * 64];
        bd.paint_base(&mut buf, 64, 64, 100, 100, 1920, 1080, true);
        assert!(buf.iter().all(|&p| (p >> 24) == 0xFF));
    }
}
