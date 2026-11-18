use std::process::Command;

/// Hide console windows for child processes on Windows.
pub trait HiddenConsole {
    fn hide_console(&mut self) -> &mut Self;
}

impl HiddenConsole for Command {
    fn hide_console(&mut self) -> &mut Self {
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            // CREATE_NO_WINDOW
            self.creation_flags(0x0800_0000);
        }
        self
    }
}
