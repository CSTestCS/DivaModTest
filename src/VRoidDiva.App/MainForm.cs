using System.Diagnostics;
using VRoidDiva.Diva;
using VRoidDiva.Vrm;

namespace VRoidDiva.App;

/// <summary>
/// One-window front end: pick a .vrm and the game folder, name the module, press Convert.
/// The mod is written straight into the game's mods folder.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox mVrmPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox mGameFolder = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Label mGameStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox mModuleName = new() { Dock = DockStyle.Fill };
    private readonly ComboBox mCharacter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };

    private readonly CheckBox mShowAdvanced = new() { Text = "Show advanced options", AutoSize = true };
    private readonly TableLayoutPanel mAdvanced = NewTable();
    private readonly TextBox mModuleId = new() { Width = 100, PlaceholderText = "automatic" };
    private readonly CheckBox mKeepProportions = new()
    {
        Text = "Keep the model's own arm and leg lengths (don't stretch to Miku's)", AutoSize = true
    };
    private readonly ComboBox mMaxTexture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Label mReferenceLabel = new() { AutoSize = true, Text = "Automatic (the game's diva_main.cpk)" };
    private List<string> mCustomReferences = new();

    private readonly Button mConvert = new()
    {
        Text = "Convert", AutoSize = true, Padding = new Padding(24, 6, 24, 6),
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold)
    };

    private readonly TextBox mLog = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window
    };

    private string mLastModFolder;

    public MainForm()
    {
        Text = "VRoidDiva: VRoid model → Project DIVA Mega Mix+ module";
        Font = SystemFonts.MessageBoxFont;
        MinimumSize = new Size(720, 560);
        Size = new Size(820, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        foreach (var character in Character.All)
            mCharacter.Items.Add(character.ModuleName);
        mCharacter.SelectedIndex = 0;

        mMaxTexture.Items.AddRange(new object[] { "4096", "2048", "1024", "512" });
        mMaxTexture.SelectedItem = "2048";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var main = NewTable();
        AddRow(main, "1. VRoid model (.vrm)", WithButton(mVrmPath, "Browse…", BrowseVrm));
        AddRow(main, "2. Game folder", WithButton(mGameFolder, "Browse…", BrowseGameFolder));
        AddRow(main, "", mGameStatus);
        AddRow(main, "3. Module name", mModuleName);
        AddRow(main, "Character", mCharacter);
        layout.Controls.Add(main);

        AddRow(mAdvanced, "Module ID", mModuleId);
        AddRow(mAdvanced, "Proportions", mKeepProportions);
        AddRow(mAdvanced, "Max texture size", mMaxTexture);
        AddRow(mAdvanced, "Skeleton source", WithButton(mReferenceLabel, "Choose files…", BrowseReferences));
        mAdvanced.Visible = false;
        mShowAdvanced.CheckedChanged += (_, _) => mAdvanced.Visible = mShowAdvanced.Checked;

        var advancedHost = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        advancedHost.Controls.Add(mShowAdvanced);
        advancedHost.Controls.Add(mAdvanced);
        layout.Controls.Add(advancedHost);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8) };
        buttons.Controls.Add(mConvert);
        layout.Controls.Add(buttons);
        layout.Controls.Add(mLog);
        Controls.Add(layout);

        mConvert.Click += async (_, _) => await ConvertAsync();
        AcceptButton = mConvert;

        DragEnter += (_, e) =>
        {
            if (DroppedVrm(e) != null)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += async (_, e) =>
        {
            if (DroppedVrm(e) is string path)
                await SelectVrmAsync(path);
        };

        Log("Pick your VRoid model (or drag the .vrm file onto this window), check the game folder, then press Convert.");
        SetGameFolder(GameLocator.FindGameFolder());
    }

    private static string DroppedVrm(DragEventArgs e) =>
        (e.Data?.GetData(DataFormats.FileDrop) as string[])?
        .FirstOrDefault(x => x.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase));

    private async void BrowseVrm()
    {
        using var dialog = new OpenFileDialog { Filter = "VRoid / VRM model (*.vrm)|*.vrm", Title = "Choose your VRoid model" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await SelectVrmAsync(dialog.FileName);
    }

    private async Task SelectVrmAsync(string path)
    {
        mVrmPath.Text = path;
        Log($"Model: {path}");

        try
        {
            var model = await Task.Run(() => VrmModel.Load(path));
            if (string.IsNullOrWhiteSpace(mModuleName.Text) || mModuleName.Tag as string == mModuleName.Text)
            {
                mModuleName.Text = Converter.DefaultName(model, path);
                mModuleName.Tag = mModuleName.Text; // remember it was filled in automatically
            }

            Log($"  VRM {model.SpecVersion}, {model.Primitives.Sum(x => x.Indices.Length / 3):N0} triangles, " +
                $"{model.Materials.Count} materials, {model.HumanBones.Count} humanoid bones.");
        }
        catch (Exception exception)
        {
            mVrmPath.Text = "";
            ShowError($"This file can't be used:\n\n{exception.Message}");
        }
    }

    private void BrowseGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = $"Choose the folder that contains {GameLocator.GameExecutable}",
            UseDescriptionForTitle = true,
            InitialDirectory = mGameFolder.Text
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!GameLocator.IsGameFolder(dialog.SelectedPath))
        {
            ShowError($"{GameLocator.GameExecutable} isn't in that folder. In Steam, right-click the game → " +
                      "Manage → Browse local files to find the right folder.");
            return;
        }

        SetGameFolder(dialog.SelectedPath);
    }

    private void SetGameFolder(string folder)
    {
        mGameFolder.Text = folder ?? "";

        if (folder == null)
        {
            mGameStatus.Text = "Game not found automatically. Press Browse… and choose the game's folder.";
            mGameStatus.ForeColor = Color.DarkOrange;
            return;
        }

        bool modLoader = GameLocator.IsModLoaderInstalled(folder);
        mGameStatus.Text = modLoader
            ? $"Found. The mod will be saved in {GameLocator.ModsFolder(folder)}"
            : "Found, but DIVA Mod Loader isn't installed in it yet (the mod won't load without it).";
        mGameStatus.ForeColor = modLoader ? Color.ForestGreen : Color.DarkOrange;
    }

    private void BrowseReferences()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "DIVA object sets (*.farc;*_obj.bin;*.cpk)|*.farc;*_obj.bin;*.cpk",
            Multiselect = true,
            Title = "Choose stock character object sets (hair, body, hands, shoes)"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        mCustomReferences = dialog.FileNames.ToList();
        mReferenceLabel.Text = string.Join(", ", mCustomReferences.Select(Path.GetFileName));
    }

    private async Task ConvertAsync()
    {
        if (!File.Exists(mVrmPath.Text))
        {
            ShowError("Choose your VRoid model (.vrm) first.");
            return;
        }

        if (!GameLocator.IsGameFolder(mGameFolder.Text))
        {
            ShowError("Choose the game folder first (the one that contains DivaMegaMix.exe).");
            return;
        }

        string name = mModuleName.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Give the module a name.");
            return;
        }

        int? moduleId = null;
        if (mModuleId.Text.Trim().Length > 0)
        {
            if (!int.TryParse(mModuleId.Text.Trim(), out int id) || id <= 0)
            {
                ShowError("The module ID must be a positive whole number, or left empty.");
                return;
            }

            moduleId = id;
        }

        var references = mCustomReferences.Count > 0
            ? mCustomReferences
            : GameLocator.FindReferenceCpk(mGameFolder.Text) is string cpk ? new List<string> { cpk } : new List<string>();

        if (references.Count == 0)
        {
            ShowError("The game's diva_main.cpk wasn't found. Open the advanced options and choose the " +
                      "skeleton source files yourself.");
            return;
        }

        string modsFolder = GameLocator.ModsFolder(mGameFolder.Text);
        string output = Path.Combine(modsFolder, GameLocator.SafeFolderName(name));

        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            var answer = MessageBox.Show(this, $"A mod folder named \"{Path.GetFileName(output)}\" already exists.\n\nReplace it?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;
            Directory.Delete(output, true);
        }

        var request = new ConvertRequest
        {
            VrmPath = mVrmPath.Text,
            OutputDirectory = output,
            References = references,
            Name = name,
            Character = (string)mCharacter.SelectedItem,
            ModuleId = moduleId,
            NoStretch = mKeepProportions.Checked,
            MaxTextureSize = int.Parse((string)mMaxTexture.SelectedItem!)
        };

        SetBusy(true);
        mLog.Clear();

        try
        {
            var result = await Task.Run(() => Converter.Run(request, message => BeginInvoke(() => Log(message))));
            mLastModFolder = output;

            string warnings = result.Warnings.Count > 0
                ? $"\n\n{result.Warnings.Count} warning(s) are listed in the log below."
                : "";

            var answer = MessageBox.Show(this,
                $"Done! \"{result.DisplayName}\" was added to your mods folder.\n\n" +
                $"Start the game and choose it on {result.Module.Character.ModuleName}'s module select screen." +
                warnings + "\n\nOpen the mod folder now?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (answer == DialogResult.Yes)
                Process.Start(new ProcessStartInfo { FileName = mLastModFolder, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log("");
            Log($"Failed: {exception.Message}");
            ShowError($"The conversion failed:\n\n{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        mConvert.Enabled = !busy;
        mConvert.Text = busy ? "Converting…" : "Convert";
        UseWaitCursor = busy;
    }

    private void Log(string message) => mLog.AppendText(message + Environment.NewLine);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static TableLayoutPanel NewTable()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3)
        }, 0, row);
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
    }

    private static Control WithButton(Control control, string text, Action onClick)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(control, 0, 0);
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => onClick();
        panel.Controls.Add(button, 1, 0);
        return panel;
    }
}
