using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Windows.Forms;

namespace IPNetworkMonitor;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class AppData
{
    public List<GroupData> Groups { get; set; } = Enumerable.Range(1, 5).Select(i => new GroupData { Id = i, Name = $"Net {i}" }).ToList();
    public Dictionary<string, Incident> OpenIncidents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Incident> History { get; set; } = new();
}
public sealed class GroupData { public int Id { get; set; } public string Name { get; set; } = "Net"; public List<string> Ips { get; set; } = new(); }
public sealed class Incident
{
    public string Ip { get; set; } = ""; public int GroupId { get; set; } public string GroupName { get; set; } = "";
    public DateTime StartedAt { get; set; } public DateTime? RecoveredAt { get; set; }
    public double DurationSeconds => Math.Max(0, ((RecoveredAt ?? DateTime.Now) - StartedAt).TotalSeconds);
}

public sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(10, 22, 39), Panel = Color.FromArgb(18, 38, 62), Panel2 = Color.FromArgb(25, 49, 77), Blue = Color.FromArgb(38, 137, 238), TextC = Color.FromArgb(229, 238, 249), Muted = Color.FromArgb(151, 174, 201), Green = Color.FromArgb(34, 197, 94), Red = Color.FromArgb(239, 68, 68), Amber = Color.FromArgb(245, 158, 11);
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPNetworkMonitor");
    readonly string dataPath; readonly string backupPath;
    readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    AppData data = new(); readonly CancellationTokenSource shutdown = new(); readonly List<GroupView> views = new();
    readonly Label internetLabel = new(); readonly Label totalsLabel = new(); readonly Label stateLabel = new(); readonly DataGridView historyGrid = new();
    readonly Button startAll = new(); readonly Button stopAll = new(); readonly Button copyLog = new(); readonly System.Windows.Forms.Timer refreshTimer = new();
    readonly ConcurrentDictionary<int, CancellationTokenSource> workers = new(); readonly ConcurrentDictionary<string, string> ipStates = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim saveLock = new(1, 1); bool closing; DateTime lastInternetCheck = DateTime.MinValue; string internetState = "Checking…";

    public MainForm()
    {
        Directory.CreateDirectory(root); dataPath = Path.Combine(root, "monitor-state.json"); backupPath = Path.Combine(root, "monitor-state.bak");
        LoadData(); BuildUi(); RefreshAll();
        Shown += (_, _) => { foreach (var g in data.Groups) StartGroup(g.Id); _ = InternetLoop(shutdown.Token); };
        FormClosing += OnClosing;
        refreshTimer.Interval = 1000; refreshTimer.Tick += (_, _) => RefreshAll(); refreshTimer.Start();
    }

    void LoadData()
    {
        try { if (File.Exists(dataPath)) data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(dataPath)) ?? new(); }
        catch { try { if (File.Exists(backupPath)) data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(backupPath)) ?? new(); } catch { data = new(); } }
        data.Groups = Enumerable.Range(1, 5).Select(i => data.Groups.FirstOrDefault(g => g.Id == i) ?? new GroupData { Id = i, Name = $"Net {i}" }).ToList();
        data.OpenIncidents = new(data.OpenIncidents ?? new(), StringComparer.OrdinalIgnoreCase); data.History ??= new();
    }
    async Task SaveData()
    {
        await saveLock.WaitAsync();
        try { var json = JsonSerializer.Serialize(data, jsonOptions); if (File.Exists(dataPath)) File.Copy(dataPath, backupPath, true); var tmp = dataPath + ".tmp"; await File.WriteAllTextAsync(tmp, json); File.Move(tmp, dataPath, true); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { saveLock.Release(); }
    }

    void BuildUi()
    {
        Text = "IP Network Monitor"; Width = 1580; Height = 920; MinimumSize = new Size(1180, 720); StartPosition = FormStartPosition.CenterScreen; BackColor = Bg; ForeColor = TextC; Font = new Font("Segoe UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi;
        var rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12), BackColor = Bg };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 68)); rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); Controls.Add(rootLayout);
        var title = new Panel { Dock = DockStyle.Fill, BackColor = Bg }; var brand = MakeLabel("◈  IP NETWORK MONITOR", 19, true, TextC); brand.Location = new Point(4, 10); brand.AutoSize = true; title.Controls.Add(brand); var sub = MakeLabel("Five independent monitoring lanes · local Windows agent", 9, false, Muted); sub.Location = new Point(8, 39); sub.AutoSize = true; title.Controls.Add(sub); rootLayout.Controls.Add(title, 0, 0);
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(12, 8, 12, 8) };
        internetLabel.Text = "●  INTERNET: CHECKING"; internetLabel.ForeColor = Amber; internetLabel.AutoSize = true; internetLabel.Location = new Point(14, 18); internetLabel.Font = new Font("Segoe UI Semibold", 10); bar.Controls.Add(internetLabel);
        totalsLabel.Text = "TOTAL 0   ONLINE 0   OFFLINE 0"; totalsLabel.AutoSize = true; totalsLabel.Location = new Point(300, 18); totalsLabel.ForeColor = TextC; bar.Controls.Add(totalsLabel);
        startAll.Text = "▶  START ALL"; StyleButton(startAll, Green); startAll.Size = new Size(125, 36); startAll.Anchor = AnchorStyles.Top | AnchorStyles.Right; startAll.Location = new Point(Width - 470, 9); startAll.Click += (_, _) => { foreach (var g in data.Groups) StartGroup(g.Id); };
        stopAll.Text = "■  STOP ALL"; StyleButton(stopAll, Red); stopAll.Size = new Size(125, 36); stopAll.Anchor = AnchorStyles.Top | AnchorStyles.Right; stopAll.Location = new Point(Width - 335, 9); stopAll.Click += (_, _) => { foreach (var g in data.Groups) StopGroup(g.Id); };
        copyLog.Text = "▣  COPY LOG"; StyleButton(copyLog, Blue); copyLog.Size = new Size(125, 36); copyLog.Anchor = AnchorStyles.Top | AnchorStyles.Right; copyLog.Location = new Point(Width - 200, 9); copyLog.Click += (_, _) => CopyReport(); bar.Controls.AddRange(new Control[] { startAll, stopAll, copyLog }); bar.Resize += (_, _) => { copyLog.Left = bar.ClientSize.Width - 137; stopAll.Left = copyLog.Left - 135; startAll.Left = stopAll.Left - 135; }; rootLayout.Controls.Add(bar, 0, 1);
        var lanes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Bg, Padding = new Padding(0, 10, 0, 10) }; for (int i = 0; i < 5; i++) lanes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        foreach (var group in data.Groups) { var v = new GroupView(group); views.Add(v); lanes.Controls.Add(v.Panel, group.Id - 1, 0); v.RenameRequested += RenameGroup; v.PasteRequested += PasteIps; v.CopyRequested += CopyIps; v.StartRequested += StartGroup; v.StopRequested += StopGroup; v.RemoveRequested += RemoveIp; v.AddRequested += AddIp; }
        rootLayout.Controls.Add(lanes, 0, 2);
        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(10) }; var header = MakeLabel("RECENT OUTAGES  ·  Copy rows directly into Excel", 11, true, TextC); header.Dock = DockStyle.Top; header.Height = 28; bottom.Controls.Add(header);
        historyGrid.Dock = DockStyle.Fill; historyGrid.BackgroundColor = Panel; historyGrid.BorderStyle = BorderStyle.None; historyGrid.AllowUserToAddRows = false; historyGrid.AllowUserToDeleteRows = false; historyGrid.ReadOnly = true; historyGrid.RowHeadersVisible = false; historyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; historyGrid.EnableHeadersVisualStyles = false; historyGrid.ColumnHeadersDefaultCellStyle.BackColor = Panel2; historyGrid.ColumnHeadersDefaultCellStyle.ForeColor = TextC; historyGrid.DefaultCellStyle.BackColor = Panel; historyGrid.DefaultCellStyle.ForeColor = TextC; historyGrid.DefaultCellStyle.SelectionBackColor = Blue; historyGrid.DefaultCellStyle.SelectionForeColor = Color.White; historyGrid.GridColor = Color.FromArgb(38, 60, 84); historyGrid.Columns.Add("date", "DATE"); historyGrid.Columns.Add("down", "FIRST FAILED"); historyGrid.Columns.Add("up", "FIRST SUCCESS"); historyGrid.Columns.Add("duration", "DURATION"); historyGrid.Columns.Add("ip", "IP ADDRESS"); historyGrid.Columns.Add("group", "NET NAME"); bottom.Controls.Add(historyGrid); rootLayout.Controls.Add(bottom, 0, 3);
    }
    static Label MakeLabel(string text, float size, bool bold, Color color) => new() { Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent };
    static void StyleButton(Button b, Color c) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = c; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(Math.Min(255,c.R+20),Math.Min(255,c.G+20),Math.Min(255,c.B+20)); b.BackColor = Color.FromArgb(25, c); b.ForeColor = Color.White; b.Font = new Font("Segoe UI Semibold", 9); b.Cursor = Cursors.Hand; }

    void RenameGroup(int id) { var g = data.Groups.First(x => x.Id == id); var name = Prompt.Show("Enter a name for this network:", "Rename network", g.Name); if (!string.IsNullOrWhiteSpace(name)) { g.Name = name.Trim(); views[id-1].SetName(g.Name); _ = SaveData(); } }
    void PasteIps(int id)
    {
        if (!Clipboard.ContainsText()) return; var raw = Clipboard.GetText(); var parsed = ParseIps(raw, out var invalid, out var duplicates); var g = data.Groups.First(x => x.Id == id); var existing = new HashSet<string>(g.Ips, StringComparer.OrdinalIgnoreCase); int added = 0;
        foreach (var ip in parsed) if (existing.Add(ip)) { g.Ips.Add(ip); added++; }
        views[id-1].SetIps(g.Ips); _ = SaveData(); MessageBox.Show($"Added: {added}\nInvalid: {invalid}\nDuplicates skipped: {duplicates + (parsed.Count - added)}", "Paste IP list", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    static List<string> ParseIps(string raw, out int invalid, out int duplicates)
    {
        invalid = 0; duplicates = 0; var result = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split(new[] { '\r','\n','\t',' ',',',';','|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { if (!IPAddress.TryParse(token, out var address)) { invalid++; continue; } var normalized = address.ToString(); if (!seen.Add(normalized)) { duplicates++; continue; } result.Add(normalized); }
        return result;
    }
    void CopyIps(int id) { var g = data.Groups.First(x => x.Id == id); Clipboard.SetText(string.Join(Environment.NewLine, g.Ips)); }
    void AddIp(int id) { var value = Prompt.Show("Enter an IP address:", "Add IP"); if (IPAddress.TryParse(value?.Trim(), out var ip)) { var g = data.Groups.First(x => x.Id == id); if (!g.Ips.Contains(ip.ToString(), StringComparer.OrdinalIgnoreCase)) { g.Ips.Add(ip.ToString()); views[id-1].SetIps(g.Ips); _ = SaveData(); } } else if (!string.IsNullOrWhiteSpace(value)) MessageBox.Show("Invalid IP address."); }
    void RemoveIp(int id, string ip) { var g = data.Groups.First(x => x.Id == id); g.Ips.RemoveAll(x => x.Equals(ip, StringComparison.OrdinalIgnoreCase)); views[id-1].SetIps(g.Ips); _ = SaveData(); }

    void StartGroup(int id)
    {
        if (workers.ContainsKey(id)) return; var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); if (!workers.TryAdd(id, cts)) { cts.Dispose(); return; }
        views[id-1].SetRunning(true); _ = Task.Run(() => MonitorGroup(id, cts.Token));
    }
    void StopGroup(int id) { if (workers.TryRemove(id, out var cts)) { cts.Cancel(); cts.Dispose(); views[id-1].SetRunning(false); } }
    async Task MonitorGroup(int id, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var g = data.Groups.First(x => x.Id == id); var snapshot = g.Ips.ToArray();
                foreach (var ip in snapshot)
                {
                    token.ThrowIfCancellationRequested(); bool success = false;
                    for (int attempt = 1; attempt <= 4; attempt++)
                    {
                        try { using var ping = new Ping(); var reply = await ping.SendPingAsync(ip, 1300); if (reply.Status == IPStatus.Success) { success = true; break; } }
                        catch (OperationCanceledException) { throw; } catch { }
                        if (attempt < 4) await Task.Delay(2000, token);
                    }
                    if (success) await ResolveIncident(ip);
                    else await OpenIncident(ip, id);
                    SetIpState(id, ip, success ? "Online" : "Offline");
                }
                if (snapshot.Length == 0) await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { if (workers.TryRemove(id, out var c)) c.Dispose(); Ui(() => views[id-1].SetRunning(false)); }
    }
    async Task OpenIncident(string ip, int groupId)
    {
        lock (data) { if (data.OpenIncidents.ContainsKey(ip)) return; var g = data.Groups.First(x => x.Id == groupId); data.OpenIncidents[ip] = new Incident { Ip = ip, GroupId = groupId, GroupName = g.Name, StartedAt = DateTime.Now }; }
        await SaveData();
    }
    async Task ResolveIncident(string ip)
    {
        bool changed = false; lock (data) { if (data.OpenIncidents.Remove(ip, out var incident)) { incident.RecoveredAt = DateTime.Now; data.History.Insert(0, incident); if (data.History.Count > 5000) data.History.RemoveRange(5000, data.History.Count - 5000); changed = true; } }
        if (changed) await SaveData();
    }
    void SetIpState(int id, string ip, string state) { ipStates[$"{id}|{ip}"] = state; Ui(RefreshAll); }
    async Task InternetLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string status = "Offline"; try { using var p = new Ping(); var r = await p.SendPingAsync("1.1.1.1", 1800); if (r.Status == IPStatus.Success) status = "Connected"; } catch { }
            internetState = status; lastInternetCheck = DateTime.Now; Ui(RefreshAll);
            try { await Task.Delay(10000, token); } catch (OperationCanceledException) { break; }
        }
    }
    void RefreshAll()
    {
        if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(RefreshAll); return; }
        int total = data.Groups.Sum(g => g.Ips.Count), online = 0, offline = 0;
        foreach (var v in views) { var g = data.Groups.First(x => x.Id == v.GroupId); var states = g.Ips.Select(ip => ipStates.TryGetValue($"{g.Id}|{ip}", out var s) ? s : "Waiting").ToArray(); online += states.Count(x => x == "Online"); offline += states.Count(x => x == "Offline"); v.UpdateStates(states, g.Ips); v.SetCounts(states.Count(x => x == "Online"), states.Count(x => x == "Offline"), g.Ips.Count); }
        internetLabel.Text = "●  INTERNET: " + internetState.ToUpperInvariant(); internetLabel.ForeColor = internetState == "Connected" ? Green : Red; totalsLabel.Text = $"TOTAL {total}     ONLINE {online}     OFFLINE {offline}     OPEN OUTAGES {data.OpenIncidents.Count}";
        historyGrid.Rows.Clear(); foreach (var i in data.History.Take(100).Concat(data.OpenIncidents.Values.OrderByDescending(x => x.StartedAt)))
        { var open = !i.RecoveredAt.HasValue; historyGrid.Rows.Add(i.StartedAt.ToString("yyyy-MM-dd"), i.StartedAt.ToString("HH:mm:ss"), open ? "Pending" : i.RecoveredAt!.Value.ToString("HH:mm:ss"), FormatDuration(i.DurationSeconds), i.Ip, i.GroupName); }
    }
    static string FormatDuration(double seconds) { var t = TimeSpan.FromSeconds(seconds); return $"{(int)t.TotalDays:00}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}"; }
    void CopyReport()
    {
        var rows = new List<string> { "Date\tFirst outage detected\tFirst successful ping\tDuration\tIP address\tNetwork name" };
        foreach (var i in data.History.OrderBy(x => x.StartedAt).Concat(data.OpenIncidents.Values.OrderBy(x => x.StartedAt))) rows.Add(string.Join("\t", i.StartedAt.ToString("yyyy-MM-dd"), i.StartedAt.ToString("HH:mm:ss"), i.RecoveredAt?.ToString("HH:mm:ss") ?? "Pending", FormatDuration(i.DurationSeconds), i.Ip, i.GroupName.Replace("\t", " ")));
        Clipboard.SetText(string.Join(Environment.NewLine, rows)); MessageBox.Show($"Copied {rows.Count - 1} outage records. Paste directly into Excel.", "Copy log", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { if (InvokeRequired) BeginInvoke(action); else action(); } catch { } } }
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return; closing = true; refreshTimer.Stop(); shutdown.Cancel(); foreach (var w in workers.Values) w.Cancel(); await SaveData();
    }
}

public sealed class GroupView
{
    public Panel Panel { get; } = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 38, 62), Margin = new Padding(4), Padding = new Padding(8) };
    public int GroupId { get; } private set; readonly Label name = new(), counts = new(); readonly ListView list = new(); readonly Button run = new(), stop = new(); readonly GroupData group;
    public event Action<int>? RenameRequested, PasteRequested, CopyRequested, StartRequested, StopRequested, AddRequested;
    public event Action<int,string>? RemoveRequested;
    public GroupView(GroupData data)
    {
        group = data; GroupId = data.Id; var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Panel.BackColor }; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); Panel.Controls.Add(layout);
        var title = new Panel { Dock = DockStyle.Fill }; name.Text = data.Name; name.Font = new Font("Segoe UI Semibold", 11); name.ForeColor = Color.FromArgb(229,238,249); name.AutoSize = true; name.Location = new Point(4, 7); name.Cursor = Cursors.Hand; name.DoubleClick += (_,_) => RenameRequested?.Invoke(GroupId); title.Controls.Add(name);
        var edit = SmallButton("✎", Color.FromArgb(38,137,238)); edit.Location = new Point(Panel.Width - 38, 2); edit.Anchor = AnchorStyles.Top | AnchorStyles.Right; edit.Click += (_,_) => RenameRequested?.Invoke(GroupId); title.Controls.Add(edit); layout.Controls.Add(title,0,0);
        counts.Dock = DockStyle.Fill; counts.ForeColor = Color.FromArgb(151,174,201); counts.Text = "Online 0  |  Offline 0"; layout.Controls.Add(counts,0,1);
        list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.GridLines = false; list.BorderStyle = BorderStyle.None; list.BackColor = Color.FromArgb(10,25,43); list.ForeColor = Color.FromArgb(229,238,249); list.Columns.Add("IP ADDRESS", 150); list.Columns.Add("STATUS", 82); list.ContextMenuStrip = new ContextMenuStrip(); list.ContextMenuStrip.Items.Add("Remove selected IP", null, (_,_) => { if (list.SelectedItems.Count > 0) RemoveRequested?.Invoke(GroupId, list.SelectedItems[0].Text); }); layout.Controls.Add(list,0,2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight }; var paste = SmallButton("Paste", Color.FromArgb(38,137,238)); paste.Click += (_,_) => PasteRequested?.Invoke(GroupId); var copy = SmallButton("Copy", Color.FromArgb(51,75,103)); copy.Click += (_,_) => CopyRequested?.Invoke(GroupId); var add = SmallButton("+ IP", Color.FromArgb(51,75,103)); add.Click += (_,_) => AddRequested?.Invoke(GroupId); actions.Controls.AddRange(new Control[] { paste, copy, add }); layout.Controls.Add(actions,0,3);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; run = SmallButton("▶ Start", Color.FromArgb(22,101,52)); run.Click += (_,_) => StartRequested?.Invoke(GroupId); stop = SmallButton("■ Stop", Color.FromArgb(127,29,29)); stop.Click += (_,_) => StopRequested?.Invoke(GroupId); controls.Controls.AddRange(new Control[] { run, stop }); layout.Controls.Add(controls,0,4); SetIps(data.Ips);
    }
    static Button SmallButton(string text, Color color) => new() { Text = text, Width = 66, Height = 29, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(2) };
    public void SetName(string value) => name.Text = value;
    public void SetIps(IEnumerable<string> ips) { list.BeginUpdate(); list.Items.Clear(); foreach (var ip in ips) list.Items.Add(new ListViewItem(new[] { ip, "Waiting" })); list.EndUpdate(); }
    public void UpdateStates(string[] states, List<string> ips) { for (int i=0; i<Math.Min(states.Length,list.Items.Count); i++) { list.Items[i].SubItems[1].Text = states[i]; list.Items[i].ForeColor = states[i] == "Online" ? Color.FromArgb(34,197,94) : states[i] == "Offline" ? Color.FromArgb(239,68,68) : Color.FromArgb(151,174,201); } }
    public void SetCounts(int online, int offline, int total) => counts.Text = $"Online {online}  |  Offline {offline}  |  Total {total}";
    public void SetRunning(bool running) { run.Enabled = !running; stop.Enabled = running; }
}

internal static class Prompt
{
    public static string? Show(string text, string caption, string initial = "")
    {
        using var f = new Form { Text = caption, Width = 390, Height = 155, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Color.FromArgb(18,38,62), ForeColor = Color.White };
        var l = new Label { Text = text, Left = 12, Top = 12, Width = 350, ForeColor = Color.White }; var t = new TextBox { Text = initial, Left = 12, Top = 40, Width = 350 }; var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 200, Top = 78, Width = 75 }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 285, Top = 78, Width = 75 }; f.Controls.AddRange(new Control[] { l,t,ok,cancel }); f.AcceptButton = ok; f.CancelButton = cancel; return f.ShowDialog() == DialogResult.OK ? t.Text : null;
    }
}
