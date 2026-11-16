//! Win32 clipboard helpers (CF_UNICODETEXT) with safe fallbacks on non-Windows.

#[cfg(windows)]
mod imp {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr;
    use windows_sys::Win32::Foundation::HANDLE;
    use windows_sys::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, GetClipboardData, OpenClipboard, SetClipboardData,
    };
    use windows_sys::Win32::System::Memory::{
        GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE,
    };
    use windows_sys::Win32::System::Ole::CF_UNICODETEXT;

    pub fn set_clipboard_text(text: &str) -> Result<(), String> {
        let wide: Vec<u16> = OsStr::new(text).encode_wide().chain(Some(0)).collect();
        let bytes_len = wide.len() * std::mem::size_of::<u16>();

        unsafe {
            if OpenClipboard(ptr::null_mut()) == 0 {
                return Err("OpenClipboard failed".into());
            }
            if EmptyClipboard() == 0 {
                CloseClipboard();
                return Err("EmptyClipboard failed".into());
            }
            let hmem = GlobalAlloc(GMEM_MOVEABLE, bytes_len);
            if hmem.is_null() {
                CloseClipboard();
                return Err("GlobalAlloc failed".into());
            }
            let locked = GlobalLock(hmem) as *mut u16;
            if locked.is_null() {
                CloseClipboard();
                return Err("GlobalLock failed".into());
            }
            ptr::copy_nonoverlapping(wide.as_ptr(), locked, wide.len());
            GlobalUnlock(hmem);

            if SetClipboardData(CF_UNICODETEXT as u32, hmem as HANDLE).is_null() {
                CloseClipboard();
                return Err("SetClipboardData failed".into());
            }
            CloseClipboard();
        }
        Ok(())
    }

    pub fn get_clipboard_text() -> Option<String> {
        unsafe {
            if OpenClipboard(ptr::null_mut()) == 0 {
                return None;
            }
            let handle = GetClipboardData(CF_UNICODETEXT as u32);
            if handle.is_null() {
                CloseClipboard();
                return None;
            }
            let ptr_u16 = GlobalLock(handle as _) as *const u16;
            let result = if ptr_u16.is_null() {
                None
            } else {
                let mut len = 0usize;
                while *ptr_u16.add(len) != 0 && len < 2_000_000 {
                    len += 1;
                }
                let slice = std::slice::from_raw_parts(ptr_u16, len);
                let s = String::from_utf16_lossy(slice);
                GlobalUnlock(handle as _);
                Some(s)
            };
            CloseClipboard();
            result
        }
    }
}

#[cfg(not(windows))]
mod imp {
    pub fn set_clipboard_text(_text: &str) -> Result<(), String> {
        Err("Clipboard only supported on Windows".into())
    }
    pub fn get_clipboard_text() -> Option<String> {
        None
    }
}

pub use imp::{get_clipboard_text, set_clipboard_text};
