using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class StartupSetup : Form
{
    private const string SettingsKey = @"Software\OdysseyVolume";
    private const string SetupState = "StartupSetupComplete";

    private readonly PictureBox headset;
    private readonly CheckBox steamVr;
    private readonly CheckBox windows;
    private readonly CheckBox runOnce;
    private readonly Button save;
    private readonly Button cancel;
    private readonly bool firstRun;
    private bool savedSteamVr;
    private bool savedWindows;
    private bool loaded;
    private bool busy;

    public bool SteamVrOnly => steamVr.Checked && !windows.Checked;

    public StartupSetup(bool firstRun)
    {
        this.firstRun = firstRun;
        Text = "Odyssey+ Volume Buttons";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        Controls.Add(layout);

        headset = new PictureBox
        {
            Size = new Size(352, 116),
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false,
            AccessibleName = "Samsung HMD Odyssey+ headset"
        };
        using (var picture = typeof(StartupSetup).Assembly.GetManifestResourceStream("OdysseyHeadset.png"))
        using (var image = Image.FromStream(picture))
            headset.Image = new Bitmap(image);
        layout.Controls.Add(headset);

        layout.Controls.Add(new Label
        {
            Text = "When should Odyssey+ Volume Buttons run?",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 12)
        });

        steamVr = AddChoice(layout, "Start and stop with SteamVR");
        windows = AddChoice(layout, "Start on user logon");
        runOnce = AddChoice(layout, "Just run once (this session)");

        if (firstRun)
        {
            layout.Controls.Add(new Label
            {
                Text = "SteamVR mode starts the app the next time SteamVR opens.",
                AutoSize = true,
                MaximumSize = new Size(352, 0),
                Margin = new Padding(0, 4, 0, 8)
            });
        }

        save = new Button { Text = firstRun ? "Continue" : "Save", AutoSize = true };
        cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 8, 0, 0)
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        layout.Controls.Add(actions);
        AcceptButton = save;
        CancelButton = cancel;

        SetBusy(false);
        save.Click += async (sender, args) => await SaveAsync();
        Shown += async (sender, args) => await ReadSettingsAsync();
    }

    public static bool NeedsFirstRun()
    {
        try
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (settings.GetValue(SetupState) is int state)
                    return state == 0;

                // An upgrade from an earlier version should keep the user's existing startup choice.
                bool alreadyConfigured = WindowsStartup.IsEnabled() || SteamVrStartup.HasManifest;
                settings.SetValue(SetupState, alreadyConfigured ? 1 : 0, RegistryValueKind.DWord);
                return !alreadyConfigured;
            }
        }
        catch (Exception error) when (IsStartupError(error))
        {
            return false;
        }
    }

    private CheckBox AddChoice(TableLayoutPanel layout, string text)
    {
        var option = new CheckBox
        {
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            Appearance = Appearance.Button,
            AutoCheck = false,
            AutoSize = true,
            UseVisualStyleBackColor = true,
            MinimumSize = new Size(352, 44),
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(0, 0, 0, 8),
            Dock = DockStyle.Fill
        };
        option.Click += (sender, args) =>
        {
            steamVr.Checked = option == steamVr;
            windows.Checked = option == windows;
            runOnce.Checked = option == runOnce;
        };
        layout.Controls.Add(option);
        return option;
    }

    private async Task ReadSettingsAsync()
    {
        SetBusy(true);
        try
        {
            await RefreshChoicesAsync();
        }
        catch (Exception error) when (IsStartupError(error))
        {
            ShowError("Could not read startup settings.", error);
        }
        finally
        {
            SetBusy(false);
        }
        if (loaded)
            ActiveControl = runOnce.Checked ? runOnce : windows.Checked ? windows : steamVr;
    }

    private async Task RefreshChoicesAsync()
    {
        loaded = false;
        savedWindows = WindowsStartup.IsEnabled();
        savedSteamVr = await Task.Run(SteamVrStartup.IsEnabled);
        steamVr.Checked = savedSteamVr;
        windows.Checked = savedWindows;
        runOnce.Checked = !savedSteamVr && !savedWindows;
        loaded = true;
    }

    private async Task SaveAsync()
    {
        if (busy || !loaded)
            return;

        bool startWithSteamVr = steamVr.Checked;
        bool startWithWindows = windows.Checked;
        bool saved = false;
        SetBusy(true);
        try
        {
            await Task.Run(() => ApplySettings(startWithSteamVr, startWithWindows));
            saved = true;
        }
        catch (Exception error) when (IsStartupError(error))
        {
            ShowError("Could not save startup settings.", error);
            try { await RefreshChoicesAsync(); }
            catch (Exception readError) when (IsStartupError(readError))
            {
                ShowError("Could not refresh startup settings.", readError);
            }
        }
        finally
        {
            SetBusy(false);
        }
        if (saved)
            DialogResult = DialogResult.OK;
    }

    private void ApplySettings(bool startWithSteamVr, bool startWithWindows)
    {
        // Enable the new startup option before removing a previously enabled option.
        if (startWithSteamVr && !savedSteamVr)
            SteamVrStartup.SetEnabled(Application.ExecutablePath, true);
        if (startWithWindows != savedWindows)
            WindowsStartup.SetEnabled(startWithWindows);
        if (!startWithSteamVr && savedSteamVr)
            SteamVrStartup.SetEnabled(Application.ExecutablePath, false);

        if (firstRun)
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
                settings.SetValue(SetupState, 1, RegistryValueKind.DWord);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UseWaitCursor = value;
        steamVr.Enabled = windows.Enabled = runOnce.Enabled = save.Enabled = loaded && !value;
        cancel.Enabled = !value;
    }

    private void ShowError(string message, Exception error)
    {
        MessageBox.Show(this, message + "\n\n" + error.Message,
            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static bool IsStartupError(Exception error)
    {
        return error is IOException || error is Win32Exception || error is UnauthorizedAccessException ||
            error is SecurityException || error is InvalidOperationException || error is ArgumentException;
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        base.OnFormClosing(args);
        if (busy && args.CloseReason == CloseReason.UserClosing)
            args.Cancel = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            headset?.Image?.Dispose();
        base.Dispose(disposing);
    }
}
