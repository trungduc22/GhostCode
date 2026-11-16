//! Persistent application configuration (`config.json`).

use serde::{Deserialize, Serialize};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum UiLanguage {
    Vi,
    En,
}

impl Default for UiLanguage {
    fn default() -> Self {
        UiLanguage::Vi
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct AppConfig {
    pub watched_dir: PathBuf,
    pub ready_dir: PathBuf,
    pub archive_dir: PathBuf,
    pub state_file: PathBuf,
    pub language: UiLanguage,
    pub dark_mode: bool,
    pub auto_copy_ready_path: bool,
    pub http_port: u16,
    pub drive_folder_name: String,
    pub drive_enabled: bool,
}

impl Default for AppConfig {
    fn default() -> Self {
        let base_docs = PathBuf::from(r"C:\Users\Duc\OneDrive\Documents");
        let archive_dir = base_docs.join("CodeArchive");
        let state_file = archive_dir.join("archive_state.json");
        Self {
            watched_dir: base_docs.clone(),
            ready_dir: base_docs.join("CodePTIT_Ready"),
            archive_dir,
            state_file,
            language: UiLanguage::Vi,
            dark_mode: true,
            auto_copy_ready_path: true,
            http_port: 48123,
            drive_folder_name: "CodeArchive".to_string(),
            drive_enabled: true,
        }
    }
}

impl AppConfig {
    /// Default config file location: `C:\Users\Duc\Documents\ssh\config.json` if that
    /// workspace exists, otherwise next to the current executable or working dir.
    pub fn default_config_path() -> PathBuf {
        let workspace_cfg = PathBuf::from(r"C:\Users\Duc\Documents\ssh\config.json");
        if Path::new(r"C:\Users\Duc\Documents\ssh").exists() {
            return workspace_cfg;
        }
        if let Ok(exe) = std::env::current_exe() {
            if let Some(dir) = exe.parent() {
                return dir.join("config.json");
            }
        }
        PathBuf::from("config.json")
    }

    pub fn load_or_default(path: &Path) -> Self {
        match fs::read_to_string(path) {
            Ok(data) => serde_json::from_str(&data).unwrap_or_default(),
            Err(_) => {
                let cfg = Self::default();
                let _ = cfg.save(path);
                cfg
            }
        }
    }

    pub fn save(&self, path: &Path) -> io::Result<()> {
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent)?;
            }
        }
        let json = serde_json::to_string_pretty(self)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, e))?;
        fs::write(path, json)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn config_roundtrip() {
        let tmp = std::env::temp_dir().join("autoname_cfg_test.json");
        let _ = fs::remove_file(&tmp);
        let mut cfg = AppConfig::default();
        assert_eq!(cfg.language, UiLanguage::Vi);
        assert_eq!(cfg.http_port, 48123);
        cfg.language = UiLanguage::En;
        cfg.dark_mode = false;
        cfg.save(&tmp).unwrap();

        let loaded = AppConfig::load_or_default(&tmp);
        assert_eq!(loaded.language, UiLanguage::En);
        assert!(!loaded.dark_mode);
        let _ = fs::remove_file(&tmp);
    }
}
