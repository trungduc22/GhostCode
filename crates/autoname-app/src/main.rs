#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use autoname_app::cli_runner;
use autoname_app::i18n::{self, Strings};
use autoname_app::mica::MicaBackdrop;
use autoname_core::config::AppConfig;
use autoname_core::context::Platform;
use autoname_core::EngineHandle;
use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::process::Command;
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use windows_sys::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows_sys::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE};
use windows_sys::Win32::Graphics::Gdi::{
    BeginPaint, BitBlt, ClientToScreen, CreateCompatibleDC, CreateDIBSection, CreateFontW,
    DeleteDC, DeleteObject, DrawTextW, EndPaint, GdiFlush, InvalidateRect, SelectObject,
    SetBkMode, SetTextColor, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, CLEARTYPE_QUALITY,
    CLIP_DEFAULT_PRECIS, DEFAULT_CHARSET, DEFAULT_PITCH, DIB_RGB_COLORS, DT_CENTER,
    DT_END_ELLIPSIS, DT_LEFT, DT_RIGHT, DT_SINGLELINE, DT_VCENTER, DT_WORDBREAK, FF_DONTCARE,
    FF_SWISS, FW_NORMAL, FW_SEMIBOLD, HBITMAP, HDC, HFONT, OUT_DEFAULT_PRECIS, PAINTSTRUCT,
    SRCCOPY, TRANSPARENT,
};
use windows_sys::Win32::System::Console::{AttachConsole, ATTACH_PARENT_PROCESS};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY,
    NOTIFYICONDATAW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyMenu, DestroyWindow,
    DispatchMessageW, GetClientRect, GetCursorPos, GetMessageW, GetSystemMetrics,
    GetWindowLongPtrW, LoadCursorW, LoadIconW, LoadImageW, PostMessageW, PostQuitMessage,
    RegisterClassW, SendMessageW, SetCursor, SetForegroundWindow, SetTimer, SetWindowLongPtrW,
    SetWindowTextW, ShowWindow, TrackPopupMenu, TranslateMessage, CS_HREDRAW, CS_VREDRAW,
    CW_USEDEFAULT, GWLP_USERDATA, HICON, IDC_ARROW, IDC_HAND, IDI_APPLICATION, IMAGE_ICON,
    LR_DEFAULTCOLOR, MF_STRING, MINMAXINFO, MSG, SM_CXSCREEN, SM_CYSCREEN, SW_HIDE, SW_RESTORE,
    SW_SHOW, TPM_BOTTOMALIGN, TPM_LEFTALIGN, WM_APP, WM_CLOSE, WM_COMMAND, WM_CREATE, WM_DESTROY,
    WM_GETMINMAXINFO, WM_LBUTTONDBLCLK, WM_LBUTTONUP, WM_MOUSEMOVE, WM_MOVE, WM_NULL, WM_PAINT,
    WM_RBUTTONUP, WM_SETICON, WM_TIMER, WNDCLASSW, WS_CAPTION, WS_MINIMIZEBOX, WS_OVERLAPPED,
    WS_SYSMENU, WS_THICKFRAME,
};

const WM_TRAYICON: u32 = WM_APP + 1;
const WM_REFRESH_UI: u32 = WM_APP + 2;
const TIMER_ID: usize = 1;
const TRAY_UID: u32 = 1001;
const ICON_BIG: usize = 1;
const ICON_SMALL: usize = 0;

const ID_TRAY_OPEN: usize = 2001;
const ID_TRAY_ORGANIZE: usize = 2002;
const ID_TRAY_SYNC: usize = 2003;
const ID_TRAY_ARCHIVE: usize = 2004;
const ID_TRAY_LANG: usize = 2005;
const ID_TRAY_EXIT: usize = 2006;

#[derive(Clone, Copy, PartialEq, Eq)]
enum NavTab {
    Overview,
    BrowserBridge,
    Archive,
    Settings,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum HitAction {
    SwitchTab(NavTab),
    OrganizeExisting,
    SyncDriveNow,
    CopyReadyPath,
    CopySourceCode,
    OpenReadyFolder,
    OpenArchiveFolder,
    RefreshBrowser,
    ToggleLanguage,
    ToggleTheme,
    ToggleAutoCopy,
    ToggleDriveEnabled,
}

#[derive(Clone, Copy)]
struct HitZone {
    id: HitAction,
    rect: RECT,
}

struct AppState {
    engine: EngineHandle,
    mica: MicaBackdrop,
    active_tab: NavTab,
    hits: Vec<HitZone>,
    hover_id: Option<HitAction>,
    busy: Arc<AtomicBool>,
    exiting: bool,
    hwnd: HWND,
    tray_icon: HICON,
}

#[derive(Clone, Copy)]
struct Rgba {
    r: u8,
    g: u8,
    b: u8,
    a: f32,
}

impl Rgba {
    const fn new(r: u8, g: u8, b: u8, a: f32) -> Self {
        Self { r, g, b, a }
    }
    const fn rgb(self) -> Rgb {
        Rgb::new(self.r, self.g, self.b)
    }
}

#[derive(Clone, Copy)]
struct Rgb {
    r: u8,
    g: u8,
    b: u8,
}

impl Rgb {
    const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }
    const fn to_u32(self) -> u32 {
        (self.b as u32) | ((self.g as u32) << 8) | ((self.r as u32) << 16) | 0xFF00_0000
    }
    const fn to_colorref(self) -> COLORREF {
        (self.r as u32) | ((self.g as u32) << 8) | ((self.b as u32) << 16)
    }
}

struct Palette {
    dark: bool,
    canvas: Rgb,
    canvas_alpha: f32,
    card: Rgb,
    card_alpha: f32,
    card_elevated: Rgb,
    border: Rgb,
    glass_border: Rgba,
    glass_top: Rgba,
    frame_border: Rgba,
    nav_hover: Rgb,
    nav_active: Rgb,
    text: Rgb,
    muted: Rgb,
    accent: Rgb,
    accent_hover: Rgb,
    accent_text: Rgb,
    btn: Rgb,
    btn_hover: Rgb,
    btn_border: Rgb,
    green: Rgb,
    green_bg: Rgb,
    amber: Rgb,
    amber_bg: Rgb,
    ptit_bg: Rgb,
    cf_bg: Rgb,
    toggle_off_track: Rgb,
    toggle_off_border: Rgb,
}

fn palette(dark: bool) -> Palette {
    if dark {
        Palette {
            dark: true,
            canvas: Rgb::new(28, 30, 42),
            canvas_alpha: 0.58,
            card: Rgb::new(42, 44, 58),
            card_alpha: 0.62,
            card_elevated: Rgb::new(52, 54, 72),
            border: Rgb::new(66, 70, 92),
            glass_border: Rgba::new(255, 255, 255, 0.08),
            glass_top: Rgba::new(255, 255, 255, 0.18),
            frame_border: Rgba::new(255, 255, 255, 0.12),
            nav_hover: Rgb::new(56, 60, 78),
            nav_active: Rgb::new(64, 68, 88),
            text: Rgb::new(244, 245, 248),
            muted: Rgb::new(172, 178, 192),
            accent: Rgb::new(96, 205, 255),
            accent_hover: Rgb::new(124, 215, 255),
            accent_text: Rgb::new(14, 20, 30),
            btn: Rgb::new(54, 56, 74),
            btn_hover: Rgb::new(70, 72, 94),
            btn_border: Rgb::new(88, 92, 118),
            green: Rgb::new(76, 208, 146),
            green_bg: Rgb::new(28, 66, 56),
            amber: Rgb::new(247, 178, 59),
            amber_bg: Rgb::new(70, 52, 28),
            ptit_bg: Rgb::new(26, 66, 88),
            cf_bg: Rgb::new(74, 54, 28),
            toggle_off_track: Rgb::new(54, 56, 74),
            toggle_off_border: Rgb::new(128, 134, 154),
        }
    } else {
        Palette {
            dark: false,
            canvas: Rgb::new(250, 251, 254),
            canvas_alpha: 0.68,
            card: Rgb::new(255, 255, 255),
            card_alpha: 0.78,
            card_elevated: Rgb::new(244, 246, 251),
            border: Rgb::new(216, 222, 232),
            glass_border: Rgba::new(0, 0, 0, 0.07),
            glass_top: Rgba::new(255, 255, 255, 0.90),
            frame_border: Rgba::new(255, 255, 255, 0.70),
            nav_hover: Rgb::new(234, 238, 246),
            nav_active: Rgb::new(255, 255, 255),
            text: Rgb::new(24, 26, 32),
            muted: Rgb::new(92, 98, 110),
            accent: Rgb::new(0, 103, 192),
            accent_hover: Rgb::new(16, 118, 208),
            accent_text: Rgb::new(255, 255, 255),
            btn: Rgb::new(244, 246, 250),
            btn_hover: Rgb::new(230, 234, 244),
            btn_border: Rgb::new(204, 210, 222),
            green: Rgb::new(16, 124, 16),
            green_bg: Rgb::new(224, 244, 228),
            amber: Rgb::new(186, 104, 0),
            amber_bg: Rgb::new(254, 241, 214),
            ptit_bg: Rgb::new(222, 240, 254),
            cf_bg: Rgb::new(254, 238, 212),
            toggle_off_track: Rgb::new(226, 229, 236),
            toggle_off_border: Rgb::new(132, 138, 152),
        }
    }
}

/// 32-bit top-down DIB buffer with anti-aliased SDF shapes + ClearType GDI text.
struct DibCanvas {
    w: i32,
    h: i32,
    pixels: &'static mut [u32],
    mem_dc: HDC,
    hbmp: HBITMAP,
    old_bmp: isize,
}

impl DibCanvas {
    unsafe fn new(screen_dc: HDC, w: i32, h: i32) -> Option<Self> {
        let mem_dc = CreateCompatibleDC(screen_dc);
        if mem_dc.is_null() {
            return None;
        }
        let mut bmi: BITMAPINFO = std::mem::zeroed();
        bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        let mut bits_ptr: *mut std::ffi::c_void = null_mut();
        let hbmp = CreateDIBSection(screen_dc, &bmi, DIB_RGB_COLORS, &mut bits_ptr, null_mut(), 0);
        if hbmp.is_null() || bits_ptr.is_null() {
            DeleteDC(mem_dc);
            return None;
        }
        let old_bmp = SelectObject(mem_dc, hbmp as _) as isize;
        let len = (w * h) as usize;
        let pixels = std::slice::from_raw_parts_mut(bits_ptr as *mut u32, len);
        SetBkMode(mem_dc, TRANSPARENT as i32);

        Some(Self {
            w,
            h,
            pixels,
            mem_dc,
            hbmp,
            old_bmp,
        })
    }

    #[inline]
    fn blend_pixel(&mut self, x: i32, y: i32, col: Rgb, alpha: f32) {
        if x < 0 || y < 0 || x >= self.w || y >= self.h || alpha <= 0.0 {
            return;
        }
        let idx = (y * self.w + x) as usize;
        if alpha >= 0.999 {
            self.pixels[idx] = col.to_u32();
            return;
        }
        let dst = self.pixels[idx];
        let db = (dst & 0xFF) as f32;
        let dg = ((dst >> 8) & 0xFF) as f32;
        let dr = ((dst >> 16) & 0xFF) as f32;
        let a = alpha.clamp(0.0, 1.0);
        let inv = 1.0 - a;
        let rb = (col.b as f32 * a + db * inv) as u32;
        let rg = (col.g as f32 * a + dg * inv) as u32;
        let rr = (col.r as f32 * a + dr * inv) as u32;
        self.pixels[idx] = rb | (rg << 8) | (rr << 16) | 0xFF00_0000;
    }

    fn fill_round_rect_frosted(
        &mut self,
        x: i32,
        y: i32,
        w: i32,
        h: i32,
        radius: f32,
        fill: Rgb,
        fill_alpha: f32,
        top_highlight: Option<Rgba>,
        border: Option<Rgba>,
    ) {
        if w <= 0 || h <= 0 {
            return;
        }
        let x0 = x.max(0);
        let y0 = y.max(0);
        let x1 = (x + w).min(self.w);
        let y1 = (y + h).min(self.h);
        let cx = x as f32 + w as f32 * 0.5;
        let cy = y as f32 + h as f32 * 0.5;
        let hx = w as f32 * 0.5;
        let hy = h as f32 * 0.5;
        let r = radius.min(hx).min(hy).max(0.0);

        for py in y0..y1 {
            let is_top_rim = (py - y) <= (r.ceil() as i32).max(2);
            for px in x0..x1 {
                let dx = (px as f32 + 0.5 - cx).abs() - hx + r;
                let dy = (py as f32 + 0.5 - cy).abs() - hy + r;
                let ox = dx.max(0.0);
                let oy = dy.max(0.0);
                let dist = (ox * ox + oy * oy).sqrt() + dx.max(dy).min(0.0) - r;
                let coverage = (0.5 - dist).clamp(0.0, 1.0);
                if coverage > 0.0 {
                    self.blend_pixel(px, py, fill, coverage * fill_alpha);
                }
                let ring = (1.0 - (dist + 0.5).abs()).clamp(0.0, 1.0);
                if ring > 0.0 {
                    if let (true, Some(hi)) = (is_top_rim, top_highlight) {
                        let t = ((py - y) as f32 / r.max(2.0)).clamp(0.0, 1.0);
                        if let Some(b) = border {
                            let mix_a = hi.a * (1.0 - t) + b.a * t;
                            let mr = (hi.r as f32 * (1.0 - t) + b.r as f32 * t) as u8;
                            let mg = (hi.g as f32 * (1.0 - t) + b.g as f32 * t) as u8;
                            let mb = (hi.b as f32 * (1.0 - t) + b.b as f32 * t) as u8;
                            self.blend_pixel(px, py, Rgb::new(mr, mg, mb), ring * mix_a);
                        } else {
                            self.blend_pixel(px, py, hi.rgb(), ring * hi.a * (1.0 - t));
                        }
                    } else if let Some(b) = border {
                        self.blend_pixel(px, py, b.rgb(), ring * b.a);
                    }
                }
            }
        }
    }

    fn fill_round_rect(
        &mut self,
        x: i32,
        y: i32,
        w: i32,
        h: i32,
        radius: f32,
        fill: Rgb,
        border: Option<Rgb>,
    ) {
        self.fill_round_rect_frosted(
            x,
            y,
            w,
            h,
            radius,
            fill,
            1.0,
            None,
            border.map(|b| Rgba::new(b.r, b.g, b.b, 1.0)),
        );
    }

    fn fill_circle(&mut self, cx: f32, cy: f32, radius: f32, col: Rgb) {
        let x0 = ((cx - radius - 1.5).floor() as i32).max(0);
        let y0 = ((cy - radius - 1.5).floor() as i32).max(0);
        let x1 = ((cx + radius + 1.5).ceil() as i32).min(self.w);
        let y1 = ((cy + radius + 1.5).ceil() as i32).min(self.h);
        for py in y0..y1 {
            for px in x0..x1 {
                let dx = px as f32 + 0.5 - cx;
                let dy = py as f32 + 0.5 - cy;
                let dist = (dx * dx + dy * dy).sqrt() - radius;
                let alpha = (0.5 - dist).clamp(0.0, 1.0);
                if alpha > 0.0 {
                    self.blend_pixel(px, py, col, alpha);
                }
            }
        }
    }

    fn draw_brand_tile(&mut self, x: i32, y: i32, size: i32) {
        let x0 = x.max(0);
        let y0 = y.max(0);
        let x1 = (x + size).min(self.w);
        let y1 = (y + size).min(self.h);
        let cx = x as f32 + size as f32 * 0.5;
        let cy = y as f32 + size as f32 * 0.5;
        let half = size as f32 * 0.5;
        let r = size as f32 * 0.24;

        for py in y0..y1 {
            for px in x0..x1 {
                let lx = (px as f32 + 0.5 - cx) / half;
                let ly = (py as f32 + 0.5 - cy) / half;
                let dx = (px as f32 + 0.5 - cx).abs() - half + r;
                let dy = (py as f32 + 0.5 - cy).abs() - half + r;
                let dist = (dx.max(0.0).powi(2) + dy.max(0.0).powi(2)).sqrt()
                    + dx.max(dy).min(0.0)
                    - r;
                let alpha = (0.5 - dist).clamp(0.0, 1.0);
                if alpha <= 0.0 {
                    continue;
                }
                let t = ((lx + 1.0) * 0.25 + (ly + 1.0) * 0.25).clamp(0.0, 1.0);
                let bg = Rgb::new(
                    (28.0 * t) as u8,
                    (95.0 * (1.0 - t) + 178.0 * t) as u8,
                    (184.0 * (1.0 - t) + 250.0 * t) as u8,
                );
                self.blend_pixel(px, py, bg, alpha);
            }
        }
    }

    fn draw_text(&self, text: &str, mut r: RECT, color: Rgb, font: HFONT, flags: u32) {
        let wide = to_wide(text);
        unsafe {
            let old_f = SelectObject(self.mem_dc, font as _);
            SetTextColor(self.mem_dc, color.to_colorref());
            DrawTextW(self.mem_dc, wide.as_ptr(), -1, &mut r, flags);
            SelectObject(self.mem_dc, old_f);
        }
    }

    unsafe fn blit_to(self, target_dc: HDC) {
        GdiFlush();
        BitBlt(target_dc, 0, 0, self.w, self.h, self.mem_dc, 0, 0, SRCCOPY);
        SelectObject(self.mem_dc, self.old_bmp as _);
        DeleteObject(self.hbmp as _);
        DeleteDC(self.mem_dc);
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(Some(0)).collect()
}

fn make_font(face_name: &str, height: i32, weight: u32) -> HFONT {
    let face = to_wide(face_name);
    let family = if face_name.contains("MDL2") {
        FF_DONTCARE
    } else {
        FF_SWISS
    };
    unsafe {
        CreateFontW(
            -height,
            0,
            0,
            0,
            weight as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET as u32,
            OUT_DEFAULT_PRECIS as u32,
            CLIP_DEFAULT_PRECIS as u32,
            CLEARTYPE_QUALITY as u32,
            (DEFAULT_PITCH | family) as u32,
            face.as_ptr(),
        )
    }
}

fn apply_dwm_theme(hwnd: HWND, dark: bool) {
    let dark_val: i32 = if dark { 1 } else { 0 };
    let corner_round: i32 = 2; // DWMWCP_ROUND
    let mica_backdrop: i32 = 2; // DWMSBT_MAINWINDOW
    unsafe {
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_IMMERSIVE_DARK_MODE as u32,
            &dark_val as *const i32 as *const _,
            std::mem::size_of::<i32>() as u32,
        );
        let _ = DwmSetWindowAttribute(
            hwnd,
            33,
            &corner_round as *const i32 as *const _,
            std::mem::size_of::<i32>() as u32,
        );
        let _ = DwmSetWindowAttribute(
            hwnd,
            38,
            &mica_backdrop as *const i32 as *const _,
            std::mem::size_of::<i32>() as u32,
        );
    }
}

fn draw_frosted_card(
    canvas: &mut DibCanvas,
    x: i32,
    y: i32,
    w: i32,
    h: i32,
    radius: f32,
    pal: &Palette,
) {
    canvas.fill_round_rect_frosted(
        x,
        y,
        w,
        h,
        radius,
        pal.card,
        pal.card_alpha,
        Some(pal.glass_top),
        Some(pal.glass_border),
    );
}

fn load_app_icon(hinst: HINSTANCE, size: i32) -> HICON {
    unsafe {
        let res_id = 1usize as *const u16;
        let sized = LoadImageW(hinst, res_id, IMAGE_ICON, size, size, LR_DEFAULTCOLOR);
        if !sized.is_null() {
            return sized as HICON;
        }
        let fallback = LoadIconW(hinst, res_id);
        if !fallback.is_null() {
            return fallback;
        }
        LoadIconW(null_mut(), IDI_APPLICATION)
    }
}

fn add_or_update_tray(hwnd: HWND, icon: HICON, msg: u32, tooltip: &str) {
    unsafe {
        let mut nid: NOTIFYICONDATAW = std::mem::zeroed();
        nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        nid.hWnd = hwnd;
        nid.uID = TRAY_UID;
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = icon;
        let tip_wide = to_wide(tooltip);
        let max = nid.szTip.len().saturating_sub(1);
        let n = tip_wide.len().min(max);
        nid.szTip[..n].copy_from_slice(&tip_wide[..n]);
        Shell_NotifyIconW(msg, &nid);
    }
}

fn remove_tray(hwnd: HWND) {
    unsafe {
        let mut nid: NOTIFYICONDATAW = std::mem::zeroed();
        nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        nid.hWnd = hwnd;
        nid.uID = TRAY_UID;
        Shell_NotifyIconW(NIM_DELETE, &nid);
    }
}

fn show_tray_menu(hwnd: HWND, s: &Strings) {
    unsafe {
        let hmenu = CreatePopupMenu();
        if hmenu.is_null() {
            return;
        }
        let items = [
            (ID_TRAY_OPEN, s.tray_open),
            (ID_TRAY_ORGANIZE, s.tray_organize),
            (ID_TRAY_SYNC, s.tray_sync),
            (ID_TRAY_ARCHIVE, s.tray_open_archive),
            (ID_TRAY_LANG, s.tray_switch_lang),
            (ID_TRAY_EXIT, s.tray_exit),
        ];
        for (id, label) in items {
            let w = to_wide(label);
            AppendMenuW(hmenu, MF_STRING, id, w.as_ptr());
        }
        let mut pt = POINT { x: 0, y: 0 };
        GetCursorPos(&mut pt);
        SetForegroundWindow(hwnd);
        TrackPopupMenu(
            hmenu,
            TPM_LEFTALIGN | TPM_BOTTOMALIGN,
            pt.x,
            pt.y,
            0,
            hwnd,
            null(),
        );
        PostMessageW(hwnd, WM_NULL, 0, 0);
        DestroyMenu(hmenu);
    }
}

fn open_folder_explorer(path: &std::path::Path) {
    let _ = std::fs::create_dir_all(path);
    let _ = Command::new("explorer.exe").arg(path).spawn();
}

fn spawn_bg_action<F>(state: &AppState, work: F)
where
    F: FnOnce(&EngineHandle) + Send + 'static,
{
    if state.busy.swap(true, Ordering::SeqCst) {
        return;
    }
    let engine = state.engine.clone();
    let busy = state.busy.clone();
    let hwnd_raw = state.hwnd as usize;
    std::thread::spawn(move || {
        work(&engine);
        busy.store(false, Ordering::SeqCst);
        unsafe {
            PostMessageW(hwnd_raw as HWND, WM_REFRESH_UI, 0, 0);
        }
    });
}

fn trigger_action(state: &mut AppState, id: HitAction) {
    let view = state.engine.view();
    let s = i18n::strings(view.config.language);

    match id {
        HitAction::SwitchTab(tab) => {
            state.active_tab = tab;
        }
        HitAction::OrganizeExisting => {
            spawn_bg_action(state, |engine| {
                let _ = engine.organize_existing();
                let _ = engine.sync_now();
            });
        }
        HitAction::SyncDriveNow => {
            spawn_bg_action(state, |engine| {
                let _ = engine.sync_now();
            });
        }
        HitAction::CopyReadyPath => {
            if let Some(p) = state.engine.copy_latest_ready_path() {
                state
                    .engine
                    .set_status_message(format!("{} {}", s.msg_copied_path, p.display()));
            } else {
                state
                    .engine
                    .set_status_message(s.msg_nothing_to_copy.to_string());
            }
        }
        HitAction::CopySourceCode => {
            if let Some(name) = state.engine.copy_latest_source_code() {
                state
                    .engine
                    .set_status_message(format!("{} ({name})", s.msg_copied_code));
            } else {
                state
                    .engine
                    .set_status_message(s.msg_nothing_to_copy.to_string());
            }
        }
        HitAction::OpenReadyFolder => {
            open_folder_explorer(&view.config.ready_dir);
        }
        HitAction::OpenArchiveFolder => {
            open_folder_explorer(&view.config.archive_dir);
        }
        HitAction::RefreshBrowser => {
            state.engine.force_scan_now();
            state.engine.set_status_message(s.msg_refreshed.to_string());
        }
        HitAction::ToggleLanguage => {
            let new_lang = state.engine.toggle_language();
            let new_s = i18n::strings(new_lang);
            let title_w = to_wide(new_s.app_title);
            unsafe {
                SetWindowTextW(state.hwnd, title_w.as_ptr());
            }
            add_or_update_tray(state.hwnd, state.tray_icon, NIM_MODIFY, new_s.tray_tooltip);
        }
        HitAction::ToggleTheme => {
            let dark = state.engine.toggle_dark_mode();
            apply_dwm_theme(state.hwnd, dark);
        }
        HitAction::ToggleAutoCopy => {
            let _ = state.engine.toggle_auto_copy();
        }
        HitAction::ToggleDriveEnabled => {
            let _ = state.engine.toggle_drive_enabled();
        }
    }
    unsafe {
        InvalidateRect(state.hwnd, null(), 0);
    }
}

fn draw_button(
    canvas: &mut DibCanvas,
    hits: &mut Vec<HitZone>,
    hover_id: Option<HitAction>,
    id: HitAction,
    rect: RECT,
    label: &str,
    primary: bool,
    pal: &Palette,
    font: HFONT,
) {
    let hovered = hover_id == Some(id);
    let fill = if primary {
        if hovered { pal.accent_hover } else { pal.accent }
    } else if hovered {
        pal.btn_hover
    } else {
        pal.btn
    };
    let border = if primary { None } else { Some(pal.btn_border) };
    let tc = if primary { pal.accent_text } else { pal.text };
    canvas.fill_round_rect(
        rect.left,
        rect.top,
        rect.right - rect.left,
        rect.bottom - rect.top,
        6.0,
        fill,
        border,
    );
    canvas.draw_text(
        label,
        rect,
        tc,
        font,
        DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );
    hits.push(HitZone { id, rect });
}

fn draw_toggle_switch(
    canvas: &mut DibCanvas,
    hits: &mut Vec<HitZone>,
    id: HitAction,
    x: i32,
    cy: i32,
    on: bool,
    label: &str,
    pal: &Palette,
    font: HFONT,
) {
    let track_w = 44;
    let track_h = 22;
    let ty = cy - track_h / 2;
    let track_fill = if on { pal.accent } else { pal.toggle_off_track };
    let track_border = if on { None } else { Some(pal.toggle_off_border) };

    canvas.fill_round_rect(x, ty, track_w, track_h, 11.0, track_fill, track_border);

    let thumb_r = 6.5;
    let thumb_cx = if on {
        x as f32 + track_w as f32 - 11.0
    } else {
        x as f32 + 11.0
    };
    let thumb_col = if on { pal.accent_text } else { pal.muted };
    canvas.fill_circle(thumb_cx, cy as f32, thumb_r, thumb_col);

    canvas.draw_text(
        label,
        RECT {
            left: x + track_w + 10,
            top: ty - 2,
            right: x + track_w + 64,
            bottom: ty + track_h + 2,
        },
        pal.text,
        font,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER,
    );

    hits.push(HitZone {
        id,
        rect: RECT {
            left: x - 4,
            top: ty - 6,
            right: x + track_w + 60,
            bottom: ty + track_h + 6,
        },
    });
}

fn draw_pill_badge(
    canvas: &mut DibCanvas,
    x: i32,
    y: i32,
    w: i32,
    h: i32,
    bg: Rgb,
    fg: Rgb,
    text: &str,
    font: HFONT,
) {
    canvas.fill_round_rect(x, y, w, h, (h as f32) * 0.5, bg, None);
    canvas.draw_text(
        text,
        RECT {
            left: x,
            top: y,
            right: x + w,
            bottom: y + h,
        },
        fg,
        font,
        DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );
}

fn paint_dashboard(hwnd: HWND, state: &mut AppState) {
    let mut ps: PAINTSTRUCT = unsafe { std::mem::zeroed() };
    let hdc = unsafe { BeginPaint(hwnd, &mut ps) };
    let mut client = RECT {
        left: 0,
        top: 0,
        right: 0,
        bottom: 0,
    };
    unsafe {
        GetClientRect(hwnd, &mut client);
    }
    let w = (client.right - client.left).max(1);
    let h = (client.bottom - client.top).max(1);

    let view = state.engine.view();
    let pal = palette(view.config.dark_mode);
    let s = i18n::strings(view.config.language);

    let Some(mut canvas) = (unsafe { DibCanvas::new(hdc, w, h) }) else {
        unsafe { EndPaint(hwnd, &ps) };
        return;
    };

    let mut origin = POINT { x: 0, y: 0 };
    let (screen_w, screen_h) = unsafe {
        ClientToScreen(hwnd, &mut origin);
        (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN))
    };
    state.mica.paint_base(
        canvas.pixels,
        w,
        h,
        origin.x,
        origin.y,
        screen_w,
        screen_h,
        pal.dark,
    );

    let f_page_title = make_font("Segoe UI", 24, FW_SEMIBOLD);
    let f_brand = make_font("Segoe UI", 16, FW_SEMIBOLD);
    let f_card_title = make_font("Segoe UI", 15, FW_SEMIBOLD);
    let f_body = make_font("Segoe UI", 14, FW_NORMAL);
    let f_small = make_font("Segoe UI", 12, FW_NORMAL);
    let f_badge = make_font("Segoe UI", 12, FW_SEMIBOLD);
    let f_mdl2 = make_font("Segoe MDL2 Assets", 16, FW_NORMAL);
    let f_mdl2_sm = make_font("Segoe MDL2 Assets", 13, FW_NORMAL);

    state.hits.clear();

    // ------------------------------------------------------------------
    // 1. Left WinUI 3 NavigationView Sidebar
    // ------------------------------------------------------------------
    let side_w = 224;
    canvas.draw_brand_tile(18, 18, 34);
    canvas.draw_text(
        "</>",
        RECT {
            left: 18,
            top: 18,
            right: 52,
            bottom: 52,
        },
        Rgb::new(255, 255, 255),
        f_badge,
        DT_CENTER | DT_SINGLELINE | DT_VCENTER,
    );
    canvas.draw_text(
        "AutoName Code",
        RECT {
            left: 62,
            top: 16,
            right: side_w - 10,
            bottom: 36,
        },
        pal.text,
        f_brand,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER,
    );
    canvas.draw_text(
        s.brand_sub,
        RECT {
            left: 62,
            top: 36,
            right: side_w - 10,
            bottom: 52,
        },
        pal.muted,
        f_small,
        DT_LEFT | DT_SINGLELINE,
    );

    let nav_items = [
        (NavTab::Overview, "\u{E80F}", s.nav_overview, 78),
        (NavTab::BrowserBridge, "\u{E774}", s.nav_browser, 124),
        (NavTab::Archive, "\u{E8B7}", s.nav_archive, 170),
        (NavTab::Settings, "\u{E713}", s.nav_settings, h - 106),
    ];

    for (tab, icon, label, ny) in nav_items {
        let nr = RECT {
            left: 10,
            top: ny,
            right: side_w - 10,
            bottom: ny + 40,
        };
        let active = state.active_tab == tab;
        let hovered = state.hover_id == Some(HitAction::SwitchTab(tab));
        if active {
            canvas.fill_round_rect_frosted(
                nr.left,
                nr.top,
                nr.right - nr.left,
                40,
                6.0,
                pal.nav_active,
                if pal.dark { 0.62 } else { 0.80 },
                Some(pal.glass_top),
                Some(pal.glass_border),
            );
            // WinUI 3 signature 3px vertical accent indicator pill
            canvas.fill_round_rect(nr.left + 2, nr.top + 10, 3, 20, 1.5, pal.accent, None);
        } else if hovered {
            canvas.fill_round_rect_frosted(
                nr.left,
                nr.top,
                nr.right - nr.left,
                40,
                6.0,
                pal.nav_hover,
                if pal.dark { 0.45 } else { 0.55 },
                None,
                None,
            );
        }
        canvas.draw_text(
            icon,
            RECT {
                left: nr.left + 14,
                top: nr.top,
                right: nr.left + 40,
                bottom: nr.bottom,
            },
            if active { pal.accent } else { pal.muted },
            f_mdl2,
            DT_CENTER | DT_SINGLELINE | DT_VCENTER,
        );
        canvas.draw_text(
            label,
            RECT {
                left: nr.left + 46,
                top: nr.top,
                right: nr.right - 8,
                bottom: nr.bottom,
            },
            pal.text,
            if active { f_card_title } else { f_body },
            DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
        );
        state.hits.push(HitZone {
            id: HitAction::SwitchTab(tab),
            rect: nr,
        });
    }

    // Bottom status card on sidebar
    let stat_r = RECT {
        left: 12,
        top: h - 54,
        right: side_w - 12,
        bottom: h - 14,
    };
    draw_frosted_card(
        &mut canvas,
        stat_r.left,
        stat_r.top,
        stat_r.right - stat_r.left,
        40,
        8.0,
        &pal,
    );
    canvas.fill_circle(
        (stat_r.left + 16) as f32,
        (stat_r.top + 20) as f32,
        4.5,
        if view.online { pal.green } else { pal.amber },
    );
    let net_summary = format!(
        "{} • {}: {}",
        if view.online {
            s.online_label
        } else {
            s.offline_label
        },
        s.total_archived_label,
        view.total_archived
    );
    canvas.draw_text(
        &net_summary,
        RECT {
            left: stat_r.left + 26,
            top: stat_r.top,
            right: stat_r.right - 8,
            bottom: stat_r.bottom,
        },
        pal.text,
        f_small,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );

    // ------------------------------------------------------------------
    // 2. Right Content Frame (WinUI 3 translucent Mica Layer 1 panel)
    // ------------------------------------------------------------------
    let cx = side_w;
    let cw_full = w - cx;
    canvas.fill_round_rect_frosted(
        cx,
        8,
        cw_full + 14,
        h + 14,
        12.0,
        pal.canvas,
        pal.canvas_alpha,
        Some(pal.frame_border),
        Some(pal.frame_border),
    );

    let px = cx + 26;
    let pw = (cw_full - 52).max(200);

    let (page_title, page_sub) = match state.active_tab {
        NavTab::Overview => (s.nav_overview, s.subtitle_overview),
        NavTab::BrowserBridge => (s.nav_browser, s.subtitle_browser),
        NavTab::Archive => (s.nav_archive, s.subtitle_archive),
        NavTab::Settings => (s.nav_settings, s.subtitle_settings),
    };

    canvas.draw_text(
        page_title,
        RECT {
            left: px,
            top: 22,
            right: px + pw - 160,
            bottom: 52,
        },
        pal.text,
        f_page_title,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER,
    );
    canvas.draw_text(
        page_sub,
        RECT {
            left: px,
            top: 54,
            right: px + pw,
            bottom: 74,
        },
        pal.muted,
        f_small,
        DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
    );

    // Pending sync pill in top-right of content area
    let sync_pill_txt = if view.pending_sync == 0 {
        format!("✓ {}", s.synced_badge)
    } else {
        format!("{}: {}", s.pending_sync_label, view.pending_sync)
    };
    draw_pill_badge(
        &mut canvas,
        px + pw - 132,
        26,
        132,
        26,
        if view.pending_sync == 0 {
            pal.green_bg
        } else {
            pal.amber_bg
        },
        if view.pending_sync == 0 {
            pal.green
        } else {
            pal.amber
        },
        &sync_pill_txt,
        f_badge,
    );

    let mut y = 86;

    match state.active_tab {
        NavTab::Overview => {
            // Hero Card: Active Problem
            let hero_h = 136;
            draw_frosted_card(&mut canvas, px, y, pw, hero_h, 8.0, &pal);
            canvas.draw_text(
                s.active_problem_card,
                RECT {
                    left: px + 16,
                    top: y + 12,
                    right: px + pw - 140,
                    bottom: y + 32,
                },
                pal.muted,
                f_small,
                DT_LEFT | DT_SINGLELINE,
            );

            if let Some(ctx) = &view.active_context {
                let (p_bg, p_fg) = match ctx.platform {
                    Platform::Ptit => (pal.ptit_bg, pal.accent),
                    Platform::Codeforces => (pal.cf_bg, pal.amber),
                    _ => (pal.green_bg, pal.green),
                };
                draw_pill_badge(
                    &mut canvas,
                    px + pw - 122,
                    y + 12,
                    106,
                    24,
                    p_bg,
                    p_fg,
                    ctx.platform.label(),
                    f_badge,
                );
                let title_line = if ctx.title.is_empty() {
                    ctx.code.clone()
                } else {
                    format!("{}  —  {}", ctx.code, ctx.title)
                };
                canvas.draw_text(
                    &title_line,
                    RECT {
                        left: px + 16,
                        top: y + 36,
                        right: px + pw - 16,
                        bottom: y + 66,
                    },
                    pal.text,
                    f_brand,
                    DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
                );
                let src_line = format!("Source: {:?}   {}", ctx.source, ctx.url);
                canvas.draw_text(
                    &src_line,
                    RECT {
                        left: px + 16,
                        top: y + 66,
                        right: px + pw - 16,
                        bottom: y + 84,
                    },
                    pal.muted,
                    f_small,
                    DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
            } else {
                canvas.draw_text(
                    s.no_active_problem,
                    RECT {
                        left: px + 16,
                        top: y + 36,
                        right: px + pw - 16,
                        bottom: y + 60,
                    },
                    pal.text,
                    f_card_title,
                    DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
                canvas.draw_text(
                    s.no_active_problem_hint,
                    RECT {
                        left: px + 16,
                        top: y + 60,
                        right: px + pw - 16,
                        bottom: y + 86,
                    },
                    pal.muted,
                    f_small,
                    DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
            }

            let bw = (pw - 44) / 2;
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::CopyReadyPath,
                RECT {
                    left: px + 16,
                    top: y + 90,
                    right: px + 16 + bw,
                    bottom: y + 124,
                },
                s.btn_copy_ptit,
                true,
                &pal,
                f_badge,
            );
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::CopySourceCode,
                RECT {
                    left: px + 28 + bw,
                    top: y + 90,
                    right: px + pw - 16,
                    bottom: y + 124,
                },
                s.btn_copy_code,
                false,
                &pal,
                f_badge,
            );
            y += hero_h + 14;

            // 2-Column Grid
            let col_gap = 14;
            let col_w = (pw - col_gap) / 2;
            let mid_h = 128;

            // Left Card: Ready File
            draw_frosted_card(&mut canvas, px, y, col_w, mid_h, 8.0, &pal);
            canvas.draw_text(
                s.ptit_ready_card,
                RECT {
                    left: px + 14,
                    top: y + 12,
                    right: px + col_w - 14,
                    bottom: y + 32,
                },
                pal.accent,
                f_card_title,
                DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
            let ready_name = view
                .latest_ready_path
                .as_ref()
                .and_then(|p| p.file_name())
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_else(|| s.no_ready_file.to_string());
            canvas.draw_text(
                &ready_name,
                RECT {
                    left: px + 14,
                    top: y + 36,
                    right: px + col_w - 14,
                    bottom: y + 76,
                },
                if view.latest_ready_path.is_some() {
                    pal.green
                } else {
                    pal.muted
                },
                f_small,
                DT_LEFT | DT_WORDBREAK,
            );
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::OpenReadyFolder,
                RECT {
                    left: px + 14,
                    top: y + 84,
                    right: px + col_w - 14,
                    bottom: y + 116,
                },
                s.btn_open_ready,
                false,
                &pal,
                f_small,
            );

            // Right Card: IDEs & Browsers
            let rx = px + col_w + col_gap;
            draw_frosted_card(&mut canvas, rx, y, col_w, mid_h, 8.0, &pal);
            canvas.draw_text(
                s.ide_browser_card,
                RECT {
                    left: rx + 14,
                    top: y + 12,
                    right: rx + col_w - 14,
                    bottom: y + 32,
                },
                pal.accent,
                f_card_title,
                DT_LEFT | DT_SINGLELINE,
            );
            let p = view.active_processes;
            let chips = [
                ("Dev-C++", p.devcpp, 0, 0),
                ("VS Code", p.vscode, 1, 0),
                ("Python", p.python, 2, 0),
                ("Edge", p.edge, 0, 1),
                ("Thorium", p.thorium, 1, 1),
            ];
            let chip_w = (col_w - 44) / 3;
            for (name, on, ci, ri) in chips {
                let cx0 = rx + 14 + ci * (chip_w + 8);
                let cy0 = y + 38 + ri * 28;
                canvas.fill_round_rect(
                    cx0,
                    cy0,
                    chip_w,
                    22,
                    11.0,
                    if on { pal.green_bg } else { pal.card_elevated },
                    Some(pal.border),
                );
                canvas.fill_circle(
                    (cx0 + 11) as f32,
                    (cy0 + 11) as f32,
                    3.5,
                    if on { pal.green } else { pal.muted },
                );
                canvas.draw_text(
                    name,
                    RECT {
                        left: cx0 + 18,
                        top: cy0,
                        right: cx0 + chip_w - 4,
                        bottom: cy0 + 22,
                    },
                    pal.text,
                    f_small,
                    DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
                );
            }
            let watch_txt = format!("{}: {}", s.watched_folder_label, view.config.watched_dir.display());
            canvas.draw_text(
                &watch_txt,
                RECT {
                    left: rx + 14,
                    top: y + 98,
                    right: rx + col_w - 14,
                    bottom: y + 118,
                },
                pal.muted,
                f_small,
                DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
            y += mid_h + 14;

            // Recent Activity Card
            let rec_h = (h - y - 42).max(116);
            draw_frosted_card(&mut canvas, px, y, pw, rec_h, 8.0, &pal);
            canvas.draw_text(
                s.recent_files_card,
                RECT {
                    left: px + 14,
                    top: y + 12,
                    right: px + pw - 14,
                    bottom: y + 32,
                },
                pal.accent,
                f_card_title,
                DT_LEFT | DT_SINGLELINE,
            );
            if view.recent_records.is_empty() {
                canvas.draw_text(
                    s.no_recent_files,
                    RECT {
                        left: px + 14,
                        top: y + 40,
                        right: px + pw - 14,
                        bottom: y + rec_h - 10,
                    },
                    pal.muted,
                    f_body,
                    DT_LEFT | DT_WORDBREAK,
                );
            } else {
                let mut ry = y + 38;
                let max_rows = ((rec_h - 46) / 26).max(1) as usize;
                for rec in view.recent_records.iter().take(max_rows) {
                    let p_bg = if rec.platform == "PTIT" {
                        pal.ptit_bg
                    } else if rec.platform == "Codeforces" {
                        pal.cf_bg
                    } else {
                        pal.card_elevated
                    };
                    let p_fg = if rec.platform == "PTIT" {
                        pal.accent
                    } else if rec.platform == "Codeforces" {
                        pal.amber
                    } else {
                        pal.muted
                    };
                    draw_pill_badge(&mut canvas, px + 14, ry + 2, 82, 20, p_bg, p_fg, &rec.platform, f_small);
                    canvas.draw_text(
                        &rec.file_name,
                        RECT {
                            left: px + 106,
                            top: ry,
                            right: px + pw - 110,
                            bottom: ry + 24,
                        },
                        pal.text,
                        f_small,
                        DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
                    );
                    let st_txt = if rec.synced_to_drive {
                        s.synced_badge
                    } else {
                        s.pending_badge
                    };
                    canvas.draw_text(
                        st_txt,
                        RECT {
                            left: px + pw - 106,
                            top: ry,
                            right: px + pw - 14,
                            bottom: ry + 24,
                        },
                        if rec.synced_to_drive { pal.green } else { pal.amber },
                        f_small,
                        DT_RIGHT | DT_SINGLELINE | DT_VCENTER,
                    );
                    ry += 26;
                }
            }
        }

        NavTab::BrowserBridge => {
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::RefreshBrowser,
                RECT {
                    left: px,
                    top: y,
                    right: px + 220,
                    bottom: y + 34,
                },
                s.btn_refresh_browser,
                true,
                &pal,
                f_badge,
            );
            y += 46;

            // Detected Browser Window Titles Card
            let card_h = 240;
            draw_frosted_card(&mut canvas, px, y, pw, card_h, 8.0, &pal);
            canvas.draw_text(
                s.detected_tabs_card,
                RECT {
                    left: px + 16,
                    top: y + 12,
                    right: px + pw - 16,
                    bottom: y + 34,
                },
                pal.accent,
                f_card_title,
                DT_LEFT | DT_SINGLELINE,
            );
            if view.browser_titles.is_empty() {
                canvas.draw_text(
                    s.no_browser_tabs,
                    RECT {
                        left: px + 16,
                        top: y + 44,
                        right: px + pw - 16,
                        bottom: y + 90,
                    },
                    pal.muted,
                    f_body,
                    DT_LEFT | DT_WORDBREAK,
                );
            } else {
                let mut ty = y + 42;
                for title in view.browser_titles.iter().take(6) {
                    canvas.fill_round_rect(
                        px + 16,
                        ty,
                        pw - 32,
                        28,
                        6.0,
                        pal.card_elevated,
                        None,
                    );
                    canvas.draw_text(
                        title,
                        RECT {
                            left: px + 26,
                            top: ty,
                            right: px + pw - 26,
                            bottom: ty + 28,
                        },
                        pal.text,
                        f_small,
                        DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
                    );
                    ty += 32;
                }
            }
            y += card_h + 14;

            // Extension Tip Card
            let tip_h = (h - y - 42).max(96);
            draw_frosted_card(&mut canvas, px, y, pw, tip_h, 8.0, &pal);
            canvas.draw_text(
                s.extension_tip_title,
                RECT {
                    left: px + 16,
                    top: y + 12,
                    right: px + pw - 16,
                    bottom: y + 32,
                },
                pal.amber,
                f_card_title,
                DT_LEFT | DT_SINGLELINE,
            );
            canvas.draw_text(
                s.extension_tip_body,
                RECT {
                    left: px + 16,
                    top: y + 38,
                    right: px + pw - 16,
                    bottom: y + tip_h - 12,
                },
                pal.text,
                f_body,
                DT_LEFT | DT_WORDBREAK,
            );
        }

        NavTab::Archive => {
            let bw = (pw - 20) / 3;
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::OrganizeExisting,
                RECT {
                    left: px,
                    top: y,
                    right: px + bw,
                    bottom: y + 34,
                },
                s.btn_organize,
                true,
                &pal,
                f_small,
            );
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::SyncDriveNow,
                RECT {
                    left: px + bw + 10,
                    top: y,
                    right: px + bw * 2 + 10,
                    bottom: y + 34,
                },
                s.btn_sync,
                false,
                &pal,
                f_small,
            );
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::OpenArchiveFolder,
                RECT {
                    left: px + bw * 2 + 20,
                    top: y,
                    right: px + pw,
                    bottom: y + 34,
                },
                s.btn_open_archive,
                false,
                &pal,
                f_small,
            );
            y += 46;

            let list_h = (h - y - 42).max(160);
            draw_frosted_card(&mut canvas, px, y, pw, list_h, 8.0, &pal);
            canvas.draw_text(
                s.recent_files_card,
                RECT {
                    left: px + 16,
                    top: y + 12,
                    right: px + pw - 16,
                    bottom: y + 34,
                },
                pal.accent,
                f_card_title,
                DT_LEFT | DT_SINGLELINE,
            );
            if view.recent_records.is_empty() {
                canvas.draw_text(
                    s.no_recent_files,
                    RECT {
                        left: px + 16,
                        top: y + 42,
                        right: px + pw - 16,
                        bottom: y + list_h - 12,
                    },
                    pal.muted,
                    f_body,
                    DT_LEFT | DT_WORDBREAK,
                );
            } else {
                let mut ry = y + 40;
                let max_rows = ((list_h - 48) / 34).max(1) as usize;
                for rec in view.recent_records.iter().take(max_rows) {
                    canvas.fill_round_rect(
                        px + 14,
                        ry,
                        pw - 28,
                        30,
                        6.0,
                        pal.card_elevated,
                        None,
                    );
                    let p_bg = if rec.platform == "PTIT" {
                        pal.ptit_bg
                    } else if rec.platform == "Codeforces" {
                        pal.cf_bg
                    } else {
                        pal.card
                    };
                    let p_fg = if rec.platform == "PTIT" {
                        pal.accent
                    } else if rec.platform == "Codeforces" {
                        pal.amber
                    } else {
                        pal.muted
                    };
                    draw_pill_badge(&mut canvas, px + 20, ry + 5, 78, 20, p_bg, p_fg, &rec.platform, f_small);
                    canvas.draw_text(
                        &rec.file_name,
                        RECT {
                            left: px + 108,
                            top: ry,
                            right: px + pw - 120,
                            bottom: ry + 30,
                        },
                        pal.text,
                        f_small,
                        DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
                    );
                    let st_txt = if rec.synced_to_drive {
                        s.synced_badge
                    } else {
                        s.pending_badge
                    };
                    canvas.draw_text(
                        st_txt,
                        RECT {
                            left: px + pw - 115,
                            top: ry,
                            right: px + pw - 22,
                            bottom: ry + 30,
                        },
                        if rec.synced_to_drive { pal.green } else { pal.amber },
                        f_small,
                        DT_RIGHT | DT_SINGLELINE | DT_VCENTER,
                    );
                    ry += 34;
                }
            }
        }

        NavTab::Settings => {
            // WinUI 3 SettingsCards
            let card_h = 70;
            let gap = 12;

            // Card 1: Language
            draw_frosted_card(&mut canvas, px, y, pw, card_h, 8.0, &pal);
            canvas.draw_text(
                "\u{F2B7}",
                RECT { left: px + 14, top: y, right: px + 46, bottom: y + card_h },
                pal.accent,
                f_mdl2,
                DT_CENTER | DT_SINGLELINE | DT_VCENTER,
            );
            canvas.draw_text(
                s.set_lang_title,
                RECT { left: px + 54, top: y + 12, right: px + pw - 190, bottom: y + 34 },
                pal.text,
                f_card_title,
                DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
            canvas.draw_text(
                s.set_lang_desc,
                RECT { left: px + 54, top: y + 36, right: px + pw - 190, bottom: y + 58 },
                pal.muted,
                f_small,
                DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
            );
            draw_button(
                &mut canvas,
                &mut state.hits,
                state.hover_id,
                HitAction::ToggleLanguage,
                RECT {
                    left: px + pw - 178,
                    top: y + 18,
                    right: px + pw - 16,
                    bottom: y + 52,
                },
                s.set_lang_btn,
                false,
                &pal,
                f_small,
            );
            y += card_h + gap;

            // Toggle SettingsCards
            let toggles = [
                (
                    HitAction::ToggleTheme,
                    "\u{E793}",
                    s.set_theme_title,
                    s.set_theme_desc,
                    view.config.dark_mode,
                ),
                (
                    HitAction::ToggleAutoCopy,
                    "\u{E8C8}",
                    s.set_autocopy_title,
                    s.set_autocopy_desc,
                    view.config.auto_copy_ready_path,
                ),
                (
                    HitAction::ToggleDriveEnabled,
                    "\u{E895}",
                    s.set_drive_title,
                    s.set_drive_desc,
                    view.config.drive_enabled,
                ),
            ];

            for (act, icon, title, desc, val) in toggles {
                draw_frosted_card(&mut canvas, px, y, pw, card_h, 8.0, &pal);
                canvas.draw_text(
                    icon,
                    RECT { left: px + 14, top: y, right: px + 46, bottom: y + card_h },
                    pal.accent,
                    f_mdl2,
                    DT_CENTER | DT_SINGLELINE | DT_VCENTER,
                );
                canvas.draw_text(
                    title,
                    RECT { left: px + 54, top: y + 12, right: px + pw - 130, bottom: y + 34 },
                    pal.text,
                    f_card_title,
                    DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
                canvas.draw_text(
                    desc,
                    RECT { left: px + 54, top: y + 36, right: px + pw - 130, bottom: y + 58 },
                    pal.muted,
                    f_small,
                    DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
                );
                draw_toggle_switch(
                    &mut canvas,
                    &mut state.hits,
                    act,
                    px + pw - 112,
                    y + card_h / 2,
                    val,
                    if val { s.toggle_on } else { s.toggle_off },
                    &pal,
                    f_small,
                );
                y += card_h + gap;
            }
        }
    }

    // Bottom status bar inside content frame
    let status = if view.status_message.is_empty() {
        s.msg_ready
    } else {
        view.status_message.as_str()
    };
    canvas.draw_text(
        "\u{E946}",
        RECT { left: px, top: h - 30, right: px + 18, bottom: h - 10 },
        pal.muted,
        f_mdl2_sm,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER,
    );
    canvas.draw_text(
        status,
        RECT { left: px + 22, top: h - 30, right: px + pw, bottom: h - 10 },
        pal.muted,
        f_small,
        DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS,
    );

    unsafe {
        canvas.blit_to(hdc);
        DeleteObject(f_page_title as _);
        DeleteObject(f_brand as _);
        DeleteObject(f_card_title as _);
        DeleteObject(f_body as _);
        DeleteObject(f_small as _);
        DeleteObject(f_badge as _);
        DeleteObject(f_mdl2 as _);
        DeleteObject(f_mdl2_sm as _);
        EndPaint(hwnd, &ps);
    }
}

fn hit_test(hits: &[HitZone], x: i32, y: i32) -> Option<HitAction> {
    hits.iter()
        .rev()
        .find(|b| x >= b.rect.left && x <= b.rect.right && y >= b.rect.top && y <= b.rect.bottom)
        .map(|b| b.id)
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut AppState;

    match msg {
        WM_CREATE => {
            SetTimer(hwnd, TIMER_ID, 1000, None);
            0
        }
        WM_GETMINMAXINFO => {
            let mmi = &mut *(lparam as *mut MINMAXINFO);
            mmi.ptMinTrackSize.x = 820;
            mmi.ptMinTrackSize.y = 540;
            0
        }
        WM_TIMER | WM_REFRESH_UI | WM_MOVE => {
            InvalidateRect(hwnd, null(), 0);
            0
        }
        WM_PAINT => {
            if !ptr.is_null() {
                paint_dashboard(hwnd, &mut *ptr);
                return 0;
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        WM_MOUSEMOVE => {
            if !ptr.is_null() {
                let st = &mut *ptr;
                let x = (lparam & 0xFFFF) as i16 as i32;
                let y = ((lparam >> 16) & 0xFFFF) as i16 as i32;
                let h = hit_test(&st.hits, x, y);
                if h != st.hover_id {
                    st.hover_id = h;
                    InvalidateRect(hwnd, null(), 0);
                }
                let cursor = LoadCursorW(null_mut(), if h.is_some() { IDC_HAND } else { IDC_ARROW });
                SetCursor(cursor);
            }
            0
        }
        WM_LBUTTONUP => {
            if !ptr.is_null() {
                let st = &mut *ptr;
                let x = (lparam & 0xFFFF) as i16 as i32;
                let y = ((lparam >> 16) & 0xFFFF) as i16 as i32;
                if let Some(id) = hit_test(&st.hits, x, y) {
                    trigger_action(st, id);
                }
            }
            0
        }
        WM_TRAYICON => {
            let ev = (lparam & 0xFFFF) as u32;
            if ev == WM_LBUTTONDBLCLK || ev == WM_LBUTTONUP {
                ShowWindow(hwnd, SW_SHOW);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            } else if ev == WM_RBUTTONUP && !ptr.is_null() {
                let st = &*ptr;
                let lang = st.engine.view().config.language;
                show_tray_menu(hwnd, i18n::strings(lang));
            }
            0
        }
        WM_COMMAND => {
            if !ptr.is_null() {
                let st = &mut *ptr;
                let cmd = wparam & 0xFFFF;
                match cmd {
                    ID_TRAY_OPEN => {
                        ShowWindow(hwnd, SW_SHOW);
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                    ID_TRAY_ORGANIZE => trigger_action(st, HitAction::OrganizeExisting),
                    ID_TRAY_SYNC => trigger_action(st, HitAction::SyncDriveNow),
                    ID_TRAY_ARCHIVE => trigger_action(st, HitAction::OpenArchiveFolder),
                    ID_TRAY_LANG => trigger_action(st, HitAction::ToggleLanguage),
                    ID_TRAY_EXIT => {
                        st.exiting = true;
                        DestroyWindow(hwnd);
                    }
                    _ => {}
                }
            }
            0
        }
        WM_CLOSE => {
            if !ptr.is_null() && !(*ptr).exiting {
                ShowWindow(hwnd, SW_HIDE);
                return 0;
            }
            DestroyWindow(hwnd);
            0
        }
        WM_DESTROY => {
            remove_tray(hwnd);
            if !ptr.is_null() {
                (*ptr).engine.stop();
            }
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() > 1 {
        unsafe {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
        if let Some(code) = cli_runner::handle_cli_args(&args) {
            std::process::exit(code);
        }
    }

    let config_path = AppConfig::default_config_path();
    let engine = EngineHandle::start(config_path);
    let view = engine.view();
    let s = i18n::strings(view.config.language);

    let class_name = to_wide("AutoNameCodeWinUI3Wnd");
    let title = to_wide(s.app_title);

    unsafe {
        let hinst: HINSTANCE = GetModuleHandleW(null());
        let big_icon = load_app_icon(hinst, 64);
        let small_icon = load_app_icon(hinst, 16);

        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: hinst,
            hIcon: big_icon,
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            hbrBackground: null_mut(),
            lpszMenuName: null(),
            lpszClassName: class_name.as_ptr(),
        };
        RegisterClassW(&wc);

        let style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_THICKFRAME;
        let hwnd = CreateWindowExW(
            0,
            class_name.as_ptr(),
            title.as_ptr(),
            style,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            920,
            620,
            null_mut(),
            null_mut(),
            hinst,
            null(),
        );
        if hwnd.is_null() {
            return;
        }

        SendMessageW(hwnd, WM_SETICON, ICON_BIG, big_icon as isize);
        SendMessageW(hwnd, WM_SETICON, ICON_SMALL, small_icon as isize);

        let mut app_state = Box::new(AppState {
            engine,
            mica: MicaBackdrop::load(),
            active_tab: NavTab::Overview,
            hits: Vec::new(),
            hover_id: None,
            busy: Arc::new(AtomicBool::new(false)),
            exiting: false,
            hwnd,
            tray_icon: small_icon,
        });

        SetWindowLongPtrW(hwnd, GWLP_USERDATA, (&mut *app_state as *mut AppState) as isize);

        apply_dwm_theme(hwnd, view.config.dark_mode);
        add_or_update_tray(hwnd, small_icon, NIM_ADD, s.tray_tooltip);
        ShowWindow(hwnd, SW_SHOW);

        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        drop(app_state);
    }
}
