using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Local Image Repair")]
[assembly: System.Reflection.AssemblyProduct("Local Image Repair")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RepairWindow());
    }
}

internal sealed class RepairWindow : Form
{
    private readonly ImageCanvas canvas = new ImageCanvas();
    private readonly ToolTip tips = new ToolTip();
    private readonly Label status, fileName, detail, format;
    private readonly InterfaceBadge zoom, state;
    private readonly InterfaceButton open, repair, select, save, original, restored, inspect, fit;
    private readonly Icon appIcon;
    private Bitmap source, output;
    private string sourcePath;
    private Rectangle resultArea;
    private bool busy;
    private enum Mood { Ready, Working, Done, Notice }

    public RepairWindow()
    {
        Text = "图像净修";
        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon != null) Icon = appIcon;
        Font = InterfaceTheme.Body;
        BackColor = InterfaceTheme.Page;
        ForeColor = InterfaceTheme.Ink;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(920, 630);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        SuspendLayout();

        var shell = Grid(1, 3);
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        Controls.Add(shell);
        shell.Controls.Add(BuildHeader(), 0, 0);

        var workspace = Grid(2, 1);
        workspace.Padding = new Padding(22, 18, 22, 14);
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        shell.Controls.Add(workspace, 0, 1);

        var preview = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0), Padding = new Padding(12, 4, 12, 3) };
        workspace.Controls.Add(preview, 0, 0);
        var viewing = Grid(1, 3);
        viewing.BackColor = InterfaceTheme.Surface;
        viewing.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        viewing.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        viewing.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        preview.Controls.Add(viewing);

        var previewHeader = Grid(2, 1);
        previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var previewTitle = Caption("图片预览", InterfaceTheme.Section, InterfaceTheme.Ink);
        previewTitle.Padding = new Padding(5, 0, 0, 0);
        previewHeader.Controls.Add(previewTitle, 0, 0);
        var views = ButtonRow(8);
        original = AddButton(views, "原图", 68, 33, Glyph.None, ButtonStyle.Quiet, "查看原图", delegate { ShowImage(false); });
        restored = AddButton(views, "修复后", 76, 33, Glyph.None, ButtonStyle.Quiet, "查看修复结果", delegate { ShowImage(true); });
        previewHeader.Controls.Add(views, 1, 0);
        viewing.Controls.Add(previewHeader, 0, 0);
        canvas.Dock = DockStyle.Fill;
        canvas.Margin = Padding.Empty;
        canvas.RegionSelected += delegate(Rectangle area) { RunRepair(area); };
        canvas.SelectionCanceled += delegate { select.Active = false; SetStatus("已退出框选。"); };
        canvas.EmptyClicked += delegate { if (!busy) OpenFile(); };
        canvas.AllowDrop = true;
        canvas.DragEnter += OnDragEnter;
        canvas.DragLeave += delegate { canvas.SetDropTarget(false); };
        canvas.DragDrop += OnDrop;
        viewing.Controls.Add(canvas, 0, 1);

        var previewFooter = Grid(2, 1);
        previewFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var navigationHint = Caption("滚轮缩放  ·  右键拖动", InterfaceTheme.Small, InterfaceTheme.Muted);
        navigationHint.Padding = new Padding(5, 0, 0, 0);
        previewFooter.Controls.Add(navigationHint, 0, 0);
        var navigation = ButtonRow(7);
        fit = AddButton(navigation, "适合窗口", 104, 34, Glyph.Fit, ButtonStyle.Quiet, "完整显示图片", delegate { canvas.Fit(); });
        inspect = AddButton(navigation, "放大检查", 104, 34, Glyph.Zoom, ButtonStyle.Quiet, "放大查看修复位置", delegate
        {
            canvas.FocusArea(resultArea.IsEmpty
                ? new Rectangle(source.Width * 3 / 4, source.Height * 4 / 5, source.Width / 4, source.Height / 5)
                : resultArea);
        });
        zoom = new InterfaceBadge { Text = "—", Size = new Size(58, 26), Margin = new Padding(7, 4, 2, 0),
            Tint = InterfaceTheme.Page, ForeColor = InterfaceTheme.Muted, AccessibleName = "预览缩放比例" };
        navigation.Controls.Add(zoom);
        canvas.ZoomChanged += delegate(float value) { zoom.Text = value > 0 ? value.ToString("P0").Replace(" %", "%") : "—"; };
        previewFooter.Controls.Add(navigation, 1, 0);
        viewing.Controls.Add(previewFooter, 0, 2);

        var sidebar = new SurfacePanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(17, 13, 17, 12) };
        workspace.Controls.Add(sidebar, 1, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, BackColor = InterfaceTheme.Surface };
        sidebar.Controls.Add(scroll);
        var tools = Grid(1, 4);
        tools.Dock = DockStyle.Top;
        tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 209));
        tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 113));
        tools.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 69));
        scroll.Controls.Add(tools);
        scroll.SizeChanged += delegate
        {
            int minimum = (int)Math.Ceiling(tools.RowStyles[0].Height + tools.RowStyles[1].Height + tools.RowStyles[3].Height);
            scroll.AutoScroll = scroll.ClientSize.Height < minimum;
            tools.Height = Math.Max(minimum, scroll.ClientSize.Height);
        };

        var actions = Grid(1, 6);
        foreach (int height in new[] { 31, 24, 46, 47, 44, 17 }) actions.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        actions.Controls.Add(Caption("修复工具", InterfaceTheme.Section, InterfaceTheme.Ink), 0, 0);
        actions.Controls.Add(Caption("识别「豆包AI生成」字样", InterfaceTheme.Small, InterfaceTheme.Muted), 0, 1);
        open = AddAction(actions, 2, "打开图片…", Glyph.Open, ButtonStyle.Outline, "打开图片（Ctrl+O）", OpenFile);
        repair = AddAction(actions, 3, "自动修复", Glyph.Sparkle, ButtonStyle.Primary, "自动查找右下角字样", delegate { RunRepair(null); });
        select = AddAction(actions, 4, "框选位置", Glyph.Select, ButtonStyle.Outline, "拖动框选需要修复的位置", BeginSelection);
        actions.Controls.Add(Divider(), 0, 5);
        tools.Controls.Add(actions, 0, 0);

        var info = Grid(1, 4);
        foreach (int height in new[] { 30, 28, 24, 27 }) info.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        info.Controls.Add(Caption("图片信息", InterfaceTheme.Section, InterfaceTheme.Ink), 0, 0);
        fileName = Caption("尚未打开图片", InterfaceTheme.Body, InterfaceTheme.Muted);
        info.Controls.Add(fileName, 0, 1);
        var infoLabels = InfoPair();
        infoLabels.Controls.Add(Caption("像素尺寸", InterfaceTheme.Small, InterfaceTheme.Muted), 0, 0);
        infoLabels.Controls.Add(Caption("格式", InterfaceTheme.Small, InterfaceTheme.Muted), 1, 0);
        info.Controls.Add(infoLabels, 0, 2);
        var infoValues = InfoPair();
        detail = Caption("—", InterfaceTheme.Body, InterfaceTheme.Ink);
        format = Caption("—", InterfaceTheme.Body, InterfaceTheme.Ink);
        infoValues.Controls.Add(detail, 0, 0);
        infoValues.Controls.Add(format, 1, 0);
        info.Controls.Add(infoValues, 0, 3);
        tools.Controls.Add(info, 0, 1);

        var export = Grid(1, 2);
        export.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        export.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        save = AddAction(export, 0, "另存为 PNG…", Glyph.Save, ButtonStyle.Outline, "保存修复结果（Ctrl+S）", SaveFile);
        var exportNote = Caption("导出保留原图尺寸", InterfaceTheme.Small, InterfaceTheme.Muted);
        exportNote.TextAlign = ContentAlignment.MiddleCenter;
        export.Controls.Add(exportNote, 0, 1);
        tools.Controls.Add(export, 0, 3);

        var footer = Grid(3, 1);
        footer.BackColor = InterfaceTheme.Surface;
        footer.Padding = new Padding(22, 0, 22, 0);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
        state = new InterfaceBadge { HasDot = true, Size = new Size(65, 25), Anchor = AnchorStyles.Left };
        footer.Controls.Add(state, 0, 0);
        status = Caption("", InterfaceTheme.Small, InterfaceTheme.Ink);
        footer.Controls.Add(status, 1, 0);
        var shortcuts = Caption("Ctrl+O 打开  ·  Ctrl+S 保存", InterfaceTheme.Small, InterfaceTheme.Muted);
        shortcuts.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(shortcuts, 2, 0);
        shell.Controls.Add(footer, 0, 2);

        SetStatus("打开或拖入一张图片，即可开始修复。");
        UpdateControls();
        ResumeLayout(true);
    }

    private static Control BuildHeader()
    {
        var header = Grid(3, 1);
        header.BackColor = InterfaceTheme.Surface;
        header.Padding = new Padding(24, 0, 24, 0);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 59));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 113));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new BrandMark { Size = new Size(45, 45), Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, 0);
        var brand = Grid(1, 2);
        brand.Dock = DockStyle.None;
        brand.Height = 51;
        brand.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        brand.Controls.Add(Caption("图像净修", InterfaceTheme.Brand, InterfaceTheme.Ink), 0, 0);
        brand.Controls.Add(Caption("本地图片修复工具", InterfaceTheme.Small, InterfaceTheme.Muted), 0, 1);
        header.Controls.Add(brand, 1, 0);
        header.Controls.Add(new InterfaceBadge { Text = "离线处理", HasDot = true, Size = new Size(104, 29),
            Anchor = AnchorStyles.Right, Tint = Color.FromArgb(231, 246, 238), ForeColor = Color.FromArgb(30, 109, 71) }, 2, 0);
        return header;
    }

    private static TableLayoutPanel Grid(int columns, int rows)
    {
        return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty };
    }

    private static Label Caption(string text, Font font, Color color)
    {
        return new Label { Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill, AutoEllipsis = true,
            Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    }

    private static TableLayoutPanel InfoPair()
    {
        var grid = Grid(2, 1);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        return grid;
    }

    private static Control Divider()
    {
        return new Panel { BackColor = InterfaceTheme.Border, Dock = DockStyle.Top, Height = 1, Margin = new Padding(0, 7, 0, 0) };
    }

    private static FlowLayoutPanel ButtonRow(int top)
    {
        return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            Margin = Padding.Empty, Padding = new Padding(0, top, 0, 0), WrapContents = false };
    }

    private InterfaceButton NewButton(string title, Glyph glyph, ButtonStyle style, string tip, Action action)
    {
        var button = new InterfaceButton(glyph, style) { Text = title, AccessibleDescription = tip };
        button.Click += delegate { action(); };
        tips.SetToolTip(button, tip);
        return button;
    }

    private InterfaceButton AddButton(Control parent, string title, int width, int height, Glyph glyph, ButtonStyle style,
        string tip, Action action)
    {
        var button = NewButton(title, glyph, style, tip, action);
        button.Size = new Size(width, height);
        button.Margin = new Padding(2, 0, 2, 0);
        parent.Controls.Add(button);
        return button;
    }

    private InterfaceButton AddAction(TableLayoutPanel parent, int row, string title, Glyph glyph, ButtonStyle style,
        string tip, Action action)
    {
        var button = NewButton(title, glyph, style, tip, action);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 3, 0, 3);
        parent.Controls.Add(button, 0, row);
        return button;
    }

    private void SetStatus(string message, Mood mood = Mood.Ready)
    {
        switch (mood)
        {
            case Mood.Working:
                state.Text = "处理中";
                state.Tint = InterfaceTheme.AccentLight;
                state.ForeColor = InterfaceTheme.Accent;
                break;
            case Mood.Done:
                state.Text = "完成";
                state.Tint = Color.FromArgb(231, 246, 238);
                state.ForeColor = Color.FromArgb(30, 109, 71);
                break;
            case Mood.Notice:
                state.Text = "提示";
                state.Tint = Color.FromArgb(251, 240, 224);
                state.ForeColor = Color.FromArgb(126, 77, 28);
                break;
            default:
                state.Text = "就绪";
                state.Tint = InterfaceTheme.Page;
                state.ForeColor = InterfaceTheme.Muted;
                break;
        }
        state.Invalidate();
        status.Text = message;
        tips.SetToolTip(status, message);
    }

    private void UpdateControls()
    {
        open.Enabled = !busy;
        repair.Enabled = select.Enabled = !busy && source != null;
        original.Enabled = fit.Enabled = inspect.Enabled = source != null;
        restored.Enabled = output != null;
        save.Enabled = !busy && output != null;
        save.Active = save.Enabled;
        canvas.AllowOpen = !busy;
        UseWaitCursor = busy;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effect = DragDropEffects.Copy;
            canvas.SetDropTarget(true);
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        canvas.SetDropTarget(false);
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (!busy && files != null && files.Length > 0) LoadFile(files[0]);
    }

    private void OpenFile()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "选择本地图片";
            dialog.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*";
            if (dialog.ShowDialog(this) == DialogResult.OK) LoadFile(dialog.FileName);
        }
    }

    private async void LoadFile(string path)
    {
        busy = true;
        canvas.CancelSelection(false);
        select.Active = false;
        UpdateControls();
        SetStatus("正在读取图片…", Mood.Working);
        try
        {
            Bitmap next = await Task.Run(() => ImageFiles.Load(path));
            canvas.SetImage(next);
            if (source != null) source.Dispose();
            if (output != null) output.Dispose();
            source = next;
            sourcePath = path;
            output = null;
            resultArea = Rectangle.Empty;
            canvas.Fit();
            ShowImage(false);
            detail.Text = string.Format("{0:N0} × {1:N0} px", next.Width, next.Height);
            fileName.Text = Path.GetFileName(path);
            fileName.ForeColor = InterfaceTheme.Ink;
            tips.SetToolTip(fileName, path);
            string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            format.Text = extension.Length == 0 ? "—" : extension;
            SetStatus("图片已打开。点击「自动修复」，或用「框选位置」指定搜索范围。");
        }
        catch (Exception ex)
        {
            ShowError("打开图片失败", ex);
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private void BeginSelection()
    {
        if (source == null || busy) return;
        ShowImage(false);
        canvas.BeginSelection();
        select.Active = true;
        SetStatus("拖动框住整行字样，四周留一点空隙。按 Esc 退出框选。");
    }

    private async void RunRepair(Rectangle? area)
    {
        if (busy || source == null) return;
        busy = true;
        canvas.CancelSelection(false);
        select.Active = false;
        UpdateControls();
        SetStatus("正在匹配字形、复查重叠字样并还原底图…", Mood.Working);
        try
        {
            using (RepairResult next = await Task.Run(() => WatermarkRepair.Run(source, area)))
            {
                if (next == null)
                {
                    SetStatus(area.HasValue
                        ? "框内未匹配到字样。请框住完整的「豆包AI生成」，并在四周留少量空隙。"
                        : "右下角未匹配到字样。若图片仍有字样，可用「框选位置」指定范围。", Mood.Notice);
                    return;
                }
                Bitmap previous = output;
                output = next.TakeImage();
                resultArea = next.Area;
                ShowImage(true);
                if (previous != null) previous.Dispose();
                SetStatus(next.LayerCount > 1
                    ? string.Format("修复完成，已处理 {0} 层字样。可放大检查后另存为 PNG。", next.LayerCount)
                    : "修复完成。可切换原图与修复后，放大检查后另存为 PNG。", Mood.Done);
            }
        }
        catch (Exception ex)
        {
            ShowError("修复失败", ex);
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private void ShowImage(bool showRestored)
    {
        if (showRestored && output == null) return;
        canvas.SetImage(showRestored ? output : source);
        original.Active = source != null && !showRestored;
        restored.Active = showRestored;
    }

    private async void SaveFile()
    {
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "保存修复后的图片";
            dialog.Filter = "PNG 图片|*.png";
            dialog.DefaultExt = "png";
            dialog.AddExtension = true;
            dialog.InitialDirectory = Path.GetDirectoryName(sourcePath);
            dialog.FileName = Path.GetFileNameWithoutExtension(sourcePath) + "_修复.png";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("请选择与原图不同的文件名或位置。", Mood.Notice);
                return;
            }
            busy = true;
            UpdateControls();
            SetStatus("正在导出 PNG…", Mood.Working);
            try
            {
                await Task.Run(() => ImageFiles.Save(output, dialog.FileName));
                SetStatus("已保存到 " + dialog.FileName, Mood.Done);
            }
            catch (Exception ex)
            {
                ShowError("保存失败", ex);
            }
            finally
            {
                busy = false;
                UpdateControls();
            }
        }
    }

    private void ShowError(string title, Exception error)
    {
        SetStatus(title + "。", Mood.Notice);
        MessageBox.Show(this, error is OutOfMemoryException ? "图片无法解码，或可用内存不足。" : error.Message,
            title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && canvas.CancelSelection(true)) return true;
        if (!busy && keyData == (Keys.Control | Keys.O)) { OpenFile(); return true; }
        if (!busy && output != null && keyData == (Keys.Control | Keys.S)) { SaveFile(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Rectangle work = Screen.FromControl(this).WorkingArea;
        if (MinimumSize.Width > work.Width || MinimumSize.Height > work.Height)
            MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
        if (Width > work.Width || Height > work.Height)
        {
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (source != null) source.Dispose();
            if (output != null) output.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing && appIcon != null) appIcon.Dispose();
    }
}

internal sealed class ImageCanvas : Control
{
    private readonly InterfaceButton choose;
    private Bitmap image;
    private float zoom;
    private PointF center, anchor;
    private Point down;
    private bool selecting, dragging, panning, dropTarget;
    private RectangleF selection;
    public event Action<Rectangle> RegionSelected;
    public event Action SelectionCanceled;
    public event Action EmptyClicked;
    public event Action<float> ZoomChanged;

    public ImageCanvas()
    {
        BackColor = InterfaceTheme.Surface;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AccessibleName = "图片预览画布";
        choose = new InterfaceButton(Glyph.Open, ButtonStyle.Primary) { Text = "选择图片", Size = new Size(132, 37) };
        choose.Click += delegate { OpenPlaceholder(); };
        Controls.Add(choose);
        Cursor = Cursors.Hand;
    }

    public bool AllowOpen
    {
        set { choose.Enabled = value; }
    }

    public void SetDropTarget(bool value)
    {
        if (dropTarget == value) return;
        dropTarget = value;
        Invalidate();
    }

    public void SetImage(Bitmap next)
    {
        image = next;
        choose.Visible = next == null;
        if (!selecting) Cursor = next == null ? Cursors.Hand : Cursors.Default;
        NotifyZoom();
        Invalidate();
    }

    public void Fit()
    {
        zoom = 0;
        NotifyZoom();
        Invalidate();
    }

    public void FocusArea(Rectangle area)
    {
        center = new PointF(area.Left + area.Width / 2f, area.Top + area.Height / 2f);
        zoom = Math.Max(1, Math.Min(3, Math.Min(Width / (area.Width + 160f), Height / (area.Height + 140f))));
        NotifyZoom();
        Invalidate();
    }

    public void BeginSelection()
    {
        selecting = true;
        Cursor = Cursors.Cross;
        Focus();
    }

    public bool CancelSelection(bool notify)
    {
        if (!selecting) return false;
        selecting = dragging = false;
        selection = RectangleF.Empty;
        Capture = false;
        Cursor = Cursors.Default;
        Invalidate();
        if (notify && SelectionCanceled != null) SelectionCanceled();
        return true;
    }

    private float ScaleFactor
    {
        get
        {
            if (zoom > 0) return zoom;
            return Math.Max(.001f, Math.Min((Width - 52f * UiScale) / image.Width, (Height - 42f * UiScale) / image.Height));
        }
    }

    private float UiScale { get { return DeviceDpi / 96f; } }

    private void NotifyZoom()
    {
        if (ZoomChanged != null) ZoomChanged(image == null ? 0 : ScaleFactor);
    }

    private RectangleF EmptyBounds
    {
        get
        {
            float width = Math.Min(352 * UiScale, Width - 22 * UiScale);
            float height = Math.Min(222 * UiScale, Height - 20 * UiScale);
            return new RectangleF((Width - width) / 2, (Height - height) / 2, width, height);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (choose == null) return;
        RectangleF bounds = EmptyBounds;
        choose.SetBounds((int)(bounds.X + (bounds.Width - 132 * UiScale) / 2),
            (int)(bounds.Bottom - 63 * UiScale), (int)(132 * UiScale), (int)(38 * UiScale));
        NotifyZoom();
    }

    private RectangleF ImageBounds
    {
        get
        {
            float scale = ScaleFactor;
            PointF focus = zoom > 0 ? center : new PointF(image.Width / 2f, image.Height / 2f);
            return new RectangleF(Width / 2f - focus.X * scale, Height / 2f - focus.Y * scale,
                image.Width * scale, image.Height * scale);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 3 || Height < 3) return;
        var g = e.Graphics;
        float s = UiScale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var viewport = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        using (var frame = InterfaceTheme.Rounded(viewport, 12 * s))
        {
            GraphicsState saved = g.Save();
            g.SetClip(frame, CombineMode.Intersect);
            using (var fill = new SolidBrush(dropTarget ? Color.FromArgb(229, 231, 255) : InterfaceTheme.Canvas))
                g.FillRectangle(fill, viewport);
            using (var dot = new SolidBrush(Color.FromArgb(213, 220, 238)))
            {
                for (float y = 13 * s; y < Height; y += 19 * s)
                for (float x = 13 * s; x < Width; x += 19 * s)
                    g.FillEllipse(dot, x, y, 1.4f * s, 1.4f * s);
            }
            if (image == null) PaintEmpty(g, s);
            else PaintImage(g, s);
            g.Restore(saved);
            using (var edge = new Pen(dropTarget ? InterfaceTheme.Accent : InterfaceTheme.Border, dropTarget ? 2 : 1))
                g.DrawPath(edge, frame);
        }
    }

    private void PaintEmpty(Graphics g, float s)
    {
        RectangleF drop = EmptyBounds;
        if (drop.Width < 5 || drop.Height < 5) return;
        using (var outline = InterfaceTheme.Rounded(drop, 17 * s))
        using (var white = new SolidBrush(InterfaceTheme.Surface))
        using (var dashed = new Pen(dropTarget ? InterfaceTheme.Accent : Color.FromArgb(162, 175, 214), 1.2f * s)
            { DashPattern = new[] { 4f, 3f } })
        {
            g.FillPath(white, outline);
            g.DrawPath(dashed, outline);
        }
        var tile = new RectangleF((Width - 49 * s) / 2, drop.Top + 25 * s, 49 * s, 49 * s);
        InterfaceTheme.Shape(g, tile, 13 * s, InterfaceTheme.AccentLight, Color.Transparent);
        InterfaceGlyphs.Draw(g, Glyph.Upload, RectangleF.Inflate(tile, -12 * s, -12 * s), InterfaceTheme.Accent);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, dropTarget ? "松开即可打开图片" : "拖入图片开始修复", InterfaceTheme.Section,
            Rectangle.Round(new RectangleF(drop.X, drop.Top + 83 * s, drop.Width, 29 * s)), InterfaceTheme.Ink, flags);
        TextRenderer.DrawText(g, "支持 PNG、JPG、BMP、TIFF", InterfaceTheme.Small,
            Rectangle.Round(new RectangleF(drop.X, drop.Top + 115 * s, drop.Width, 23 * s)), InterfaceTheme.Muted, flags);
    }

    private void PaintImage(Graphics g, float s)
    {
        var bounds = ImageBounds;
        if (zoom == 0)
        {
            for (int i = 4; i >= 1; i--)
            {
                var shade = RectangleF.Inflate(bounds, i * s, i * s);
                shade.Offset(0, 3 * s);
                InterfaceTheme.Shape(g, shade, 4 * s, Color.FromArgb(12, 27, 42, 76), Color.Transparent);
            }
        }
        g.InterpolationMode = ScaleFactor >= 1.5f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var paper = new SolidBrush(Color.White)) g.FillRectangle(paper, bounds);
        lock (image)
            g.DrawImage(image, bounds, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
        if (dragging)
        {
            var rect = new RectangleF(bounds.X + selection.X * ScaleFactor, bounds.Y + selection.Y * ScaleFactor,
                selection.Width * ScaleFactor, selection.Height * ScaleFactor);
            using (var tint = new SolidBrush(Color.FromArgb(55, 94, 90, 238))) g.FillRectangle(tint, rect);
            using (var under = new Pen(Color.White, 3 * s)) g.DrawRectangle(under, rect.X, rect.Y, rect.Width, rect.Height);
            using (var edge = new Pen(InterfaceTheme.Accent, 2 * s) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private void OpenPlaceholder()
    {
        if (image == null && choose.Enabled && EmptyClicked != null) EmptyClicked();
    }

    private PointF ToImage(Point point, bool clamp)
    {
        RectangleF bounds = ImageBounds;
        float x = (point.X - bounds.X) / ScaleFactor;
        float y = (point.Y - bounds.Y) / ScaleFactor;
        return clamp ? new PointF(Math.Max(0, Math.Min(x, image.Width)), Math.Max(0, Math.Min(y, image.Height))) : new PointF(x, y);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (image == null)
        {
            if (e.Button == MouseButtons.Left) OpenPlaceholder();
            return;
        }
        Focus();
        if (e.Button == MouseButtons.Left && selecting && ImageBounds.Contains(e.Location))
        {
            dragging = true;
            anchor = ToImage(e.Location, true);
            selection = RectangleF.Empty;
            Capture = true;
        }
        else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
        {
            anchor = ToImage(new Point(Width / 2, Height / 2), false);
            zoom = ScaleFactor;
            center = anchor;
            down = e.Location;
            panning = true;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            PointF point = ToImage(e.Location, true);
            selection = RectangleF.FromLTRB(Math.Min(anchor.X, point.X), Math.Min(anchor.Y, point.Y),
                Math.Max(anchor.X, point.X), Math.Max(anchor.Y, point.Y));
            Invalidate();
        }
        else if (panning)
        {
            center = new PointF(Math.Max(0, Math.Min(image.Width, anchor.X - (e.X - down.X) / zoom)),
                Math.Max(0, Math.Min(image.Height, anchor.Y - (e.Y - down.Y) / zoom)));
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (panning && (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle))
        {
            panning = false;
            Capture = false;
        }
        if (!dragging || e.Button != MouseButtons.Left) return;
        var area = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.Round(selection));
        dragging = false;
        Capture = false;
        Invalidate();
        if (area.Width < 24 || area.Height < 8) return;
        CancelSelection(false);
        if (RegionSelected != null) RegionSelected(area);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (image == null || dragging || panning) return;
        PointF point = ToImage(e.Location, false);
        float scale = ScaleFactor;
        zoom = Math.Max(Math.Min(scale, .07f), Math.Min(12, scale * (float)Math.Pow(1.2, e.Delta / 120.0)));
        center = new PointF(Math.Max(0, Math.Min(image.Width, point.X + (Width / 2f - e.X) / zoom)),
            Math.Max(0, Math.Min(image.Height, point.Y + (Height / 2f - e.Y) / zoom)));
        NotifyZoom();
        Invalidate();
    }
}

internal static class ImageFiles
{
    public static Bitmap Load(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var decoded = Image.FromStream(stream, true, true))
        {
            if ((long)decoded.Width * decoded.Height > 40000000 || decoded.Width > 16000 || decoded.Height > 16000)
                throw new InvalidDataException("图片最大支持 4,000 万像素，单边不超过 16,000 像素。");
            const int orientation = 0x0112;
            if (Array.IndexOf(decoded.PropertyIdList, orientation) >= 0)
            {
                byte[] value = decoded.GetPropertyItem(orientation).Value;
                if (value.Length != 0)
                {
                    RotateFlipType[] transforms = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                        RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                        RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX,
                        RotateFlipType.Rotate270FlipNone };
                    if (value[0] > 1 && value[0] < transforms.Length) decoded.RotateFlip(transforms[value[0]]);
                }
            }
            var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(copy))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(decoded, new Rectangle(0, 0, copy.Width, copy.Height), 0, 0, decoded.Width, decoded.Height, GraphicsUnit.Pixel);
                }
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }
    }

    public static void Save(Bitmap image, string path)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path), ".image-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            lock (image) image.Save(temporary, ImageFormat.Png);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
