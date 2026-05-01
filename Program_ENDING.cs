            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "FileserverDriveManagerMutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Fileserver Drive Manager is already running.", "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.Run(new MainForm());
            }
        }
    }
}
