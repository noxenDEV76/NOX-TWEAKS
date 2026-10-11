using System.Text.Json;
using NoxTweak.Core.Ai;
using NoxTweak.Core.Engine;
using NoxTweak.Core.GameMode;
using NoxTweak.Core.Games;
using NoxTweak.Core.Logging;
using NoxTweak.Core.Model;
using NoxTweak.Core.Network;
using NoxTweak.Core.Optimizations;
using NoxTweak.Core.Recommendations;
using NoxTweak.Core.Registry;
using NoxTweak.Core.Settings;
using NoxTweak.Core.Startup;
using NoxTweak.Core.SystemInfo;
using NoxTweak.UI;
using System.Windows.Input;

// Dependency-free test runner so it works in any environment (including offline ones).
var passed = 0; var failed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"  PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}"); }
}
void True(bool c, string msg = "assertion failed") { if (!c) throw new Exception(msg); }
void Eq<T>(T expected, T actual, string? what = null) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what ?? "value"}: expected <{expected}> but was <{actual}>"); }

string Tmp() { var d = Path.Combine(Path.GetTempPath(), "nox-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

(OptimizationEngine eng, InMemoryRegistry reg, FakePowerPlanService pwr, BackupStore store, FixedHostInfo host) NewEngine(bool admin = false, int build = 22631)
{
    var reg = new InMemoryRegistry(); var pwr = new FakePowerPlanService(); var store = new BackupStore(Tmp());
    var host = new FixedHostInfo(build, admin);
    return (new OptimizationEngine(reg, pwr, store, host, new AppLog(null)), reg, pwr, store, host);
}

Console.WriteLine("NOX TWEAK core self-tests\n");

// ------------------------------------------------------------------ catalog
Console.WriteLine("Catalog");
Test("ids are unique", () => Eq(Catalog.All.Count, Catalog.All.Select(d => d.Id).Distinct().Count()));
Test("every definition changes something and explains itself", () =>
{
    foreach (var d in Catalog.All)
    {
        True(d.Changes.Count > 0 || d.PowerPlanGuid is not null, d.Id + " has no action");
        True(d.Description.Length > 20 && d.Technical.Length > 20 && d.Justification.Length > 10, d.Id + " lacks explanation");
    }
});
Test("no catalog entry touches security-critical keys", () =>
{
    string[] forbidden = ["Windows Defender", "Policies\\Microsoft\\Windows Defender", "WindowsUpdate", "MpsSvc", "SecureBoot", "DeviceGuard", "Memory Management", "Session Manager\\Kernel", "Services\\"];
    foreach (var c in Catalog.All.SelectMany(d => d.Changes))
        foreach (var f in forbidden) True(!c.Path.Contains(f, StringComparison.OrdinalIgnoreCase), $"{c.Path} contains {f}");
});
Test("only allow-listed power GUIDs", () =>
{
    foreach (var d in Catalog.All.Where(d => d.PowerPlanGuid is not null)) True(PowerGuids.Allowed.Contains(d.PowerPlanGuid!));
});
Test("HKLM tweaks are flagged as needing admin", () =>
{
    foreach (var d in Catalog.All.Where(d => d.Changes.Any(c => c.Hive == RegHive.LocalMachine))) True(d.NeedsAdminRights);
});

// ------------------------------------------------------------------- engine
Console.WriteLine("\nEngine: apply / verify / restore");
Test("apply writes every value and restore removes values that did not exist", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    var d = Catalog.Find("core.gamedvr")!;
    Eq(OptState.NotApplied, eng.GetState(d));
    var r = eng.Apply(d);
    Eq(OpOutcome.Success, r.Outcome, r.Message);
    Eq(OptState.Applied, eng.GetState(d));
    var rr = eng.Restore(d);
    Eq(OpOutcome.Success, rr.Outcome, rr.Message);
    Eq(0, reg.Count, "values created by the tweak must be deleted, not defaulted");
    Eq(OptState.NotApplied, eng.GetState(d));
});
Test("restore puts back the exact original value and type", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    reg.Seed(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", new RegValue(RegKind.String, "1"));
    reg.Seed(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", new RegValue(RegKind.String, "6"));
    var d = Catalog.Find("qol.mouseaccel")!;
    True(eng.Apply(d).Ok);
    Eq("0", (string)reg.Read(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed")!.Data);
    True(eng.Restore(d).Ok);
    Eq("1", (string)reg.Read(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed")!.Data);
    Eq("6", (string)reg.Read(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1")!.Data);
    True(reg.Read(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2") is null, "MouseThreshold2 did not exist originally");
});
Test("mid-way write failure rolls everything back", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    var d = Catalog.Find("priv.tips")!;
    reg.Seed(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", new RegValue(RegKind.DWord, 1));
    reg.FailWritesTo(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled");
    var r = eng.Apply(d);
    Eq(OpOutcome.RolledBack, r.Outcome, r.Message);
    Eq(1, Convert.ToInt32(reg.Read(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled")!.Data));
    Eq(1, reg.Count, "no leftovers from the failed attempt");
});
Test("read-back mismatch is reported as failure, never as success", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    var d = Catalog.Find("core.transparency")!;
    reg.IgnoreWritesTo(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
    var r = eng.Apply(d);
    True(!r.Ok, "must not claim success");
    Eq(OptState.NotApplied, eng.GetState(d));
});
Test("HKLM change without admin is blocked and flags elevation", () =>
{
    var (eng, reg, _, _, _) = NewEngine(admin: false);
    var r = eng.Apply(Catalog.Find("adv.hags")!);
    Eq(OpOutcome.Blocked, r.Outcome); True(r.NeedsElevation); Eq(0, reg.Count);
});
Test("HKLM change with admin works and reports restart required", () =>
{
    var (eng, reg, _, _, _) = NewEngine(admin: true);
    var r = eng.Apply(Catalog.Find("adv.hags")!);
    True(r.Ok, r.Message); Eq(RestartNeed.Required, r.Restart);
    True(eng.Restore(Catalog.Find("adv.hags")!).Ok); Eq(0, reg.Count);
});
Test("Windows build gating", () =>
{
    var (eng, _, _, _, _) = NewEngine(build: 18363);
    Eq(OptState.Unsupported, eng.GetState(Catalog.Find("adv.hags")!));
    Eq(OpOutcome.Blocked, eng.Apply(Catalog.Find("adv.hags")!).Outcome);
    var (eng11, _, _, _, _) = NewEngine(build: 22631);
    Eq(OptState.Unsupported, eng11.GetState(Catalog.Find("adv.bgapps")!));
});
Test("existing value with unexpected type is never overwritten", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    reg.Seed(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", new RegValue(RegKind.String, "weird"));
    var r = eng.Apply(Catalog.Find("priv.adid")!);
    Eq(OpOutcome.Blocked, r.Outcome);
    Eq("weird", (string)reg.Read(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled")!.Data);
});
Test("applying twice does not create a second backup", () =>
{
    var (eng, _, _, store, _) = NewEngine();
    var d = Catalog.Find("core.gamemode")!;
    eng.Apply(d); eng.Apply(d);
    Eq(1, store.List().Count);
});
Test("restore without a NOX backup is refused honestly", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    reg.Seed(RegHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", new RegValue(RegKind.DWord, 1));
    reg.Seed(RegHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", new RegValue(RegKind.DWord, 1));
    var r = eng.Restore(Catalog.Find("core.gamemode")!);
    Eq(OpOutcome.Blocked, r.Outcome);
});
Test("conflict detection blocks and names the conflicting tweak", () =>
{
    var (eng, _, _, _, _) = NewEngine();
    var a = Catalog.Find("core.gamemode")!;
    eng.Apply(a);
    var b = new OptimizationDefinition { Id = "test.b", Title = "B", Description = "x".PadRight(30), Technical = "x".PadRight(30), Justification = "x".PadRight(30),
        Changes = [new RegistryChange(RegHive.CurrentUser, @"Software\Test", "B", RegKind.DWord, 1)], ConflictsWith = ["core.gamemode"] };
    var r = eng.Apply(b, [a, b]);
    Eq(OpOutcome.Blocked, r.Outcome); True(r.Message.Contains("Enable Windows Game Mode"));
});

Console.WriteLine("\nEngine: power plan");
Test("power plan switch is verified and restored", () =>
{
    var (eng, _, pwr, _, _) = NewEngine();
    var d = Catalog.Find("power.high")!;
    True(eng.Apply(d).Ok); Eq(PowerGuids.HighPerformance, pwr.Active);
    True(eng.Restore(d).Ok); Eq(PowerGuids.Balanced, pwr.Active);
});
Test("power plan failure leaves the plan unchanged", () =>
{
    var (eng, _, pwr, _, _) = NewEngine();
    pwr.FailNext = true;
    var r = eng.Apply(Catalog.Find("power.high")!);
    True(!r.Ok); Eq(PowerGuids.Balanced, pwr.Active);
});
Test("power service refuses GUIDs that are not allow-listed", () =>
{
    var pwr = new FakePowerPlanService();
    try { pwr.SetActive("00000000-0000-0000-0000-000000000000"); throw new Exception("accepted"); }
    catch (InvalidOperationException) { }
});

Console.WriteLine("\nBackups");
Test("tampered backup is rejected", () =>
{
    var (eng, reg, _, store, _) = NewEngine();
    var d = Catalog.Find("priv.adid")!;
    reg.Seed(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", new RegValue(RegKind.DWord, 1));
    var id = eng.Apply(d).BackupId!;
    var path = Path.Combine(store.Directory_, id + ".json");
    File.WriteAllText(path, File.ReadAllText(path).Replace("\"Data\": \"1\"", "\"Data\": \"0\""));
    var r = eng.RestoreBackup(id);
    Eq(OpOutcome.Blocked, r.Outcome); True(r.Message.Contains("integrity"));
    Eq(0, Convert.ToInt32(reg.Read(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled")!.Data), "tampered restore must not run");
});
Test("restored backup cannot be restored twice", () =>
{
    var (eng, _, _, _, _) = NewEngine();
    var d = Catalog.Find("core.gamemode")!;
    var id = eng.Apply(d).BackupId!;
    True(eng.RestoreBackup(id).Ok);
    Eq(OpOutcome.Blocked, eng.RestoreBackup(id).Outcome);
});
Test("unwritable backup folder blocks the change before touching anything", () =>
{
    var reg = new InMemoryRegistry(); var dir = Tmp(); var store = new BackupStore(dir);
    Directory.Delete(dir, true);
    File.WriteAllText(dir, "i am a file, not a folder"); // saving into it must fail
    var eng = new OptimizationEngine(reg, new FakePowerPlanService(), store, new FixedHostInfo(22631, false), new AppLog(null));
    var r = eng.Apply(Catalog.Find("core.gamemode")!);
    Eq(OpOutcome.Blocked, r.Outcome); Eq(0, reg.Count);
});

// ----------------------------------------------------------------- startup
Console.WriteLine("\nStartup apps");
Test("lists per-user entries with enabled state and restores exact state", () =>
{
    var (eng, reg, _, _, _) = NewEngine();
    reg.Seed(RegHive.CurrentUser, StartupService.RunPath, "Discord", new RegValue(RegKind.String, @"C:\x\Discord.exe"));
    reg.Seed(RegHive.CurrentUser, StartupService.RunPath, "Steam", new RegValue(RegKind.String, @"C:\x\steam.exe"));
    reg.Seed(RegHive.LocalMachine, StartupService.RunPath, "Vendor", new RegValue(RegKind.String, @"C:\v.exe"));
    reg.Seed(RegHive.CurrentUser, StartupService.ApprovedPath, "Steam", new RegValue(RegKind.Binary, new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }));
    var list = StartupService.List(reg);
    Eq(3, list.Count);
    True(list.Single(e => e.Name == "Discord").Enabled);
    True(!list.Single(e => e.Name == "Steam").Enabled);
    True(!list.Single(e => e.Name == "Vendor").CanManage, "machine-wide entries are read-only");

    var dis = StartupService.DefinitionForNow("Discord");
    True(eng.Apply(dis).Ok);
    True(!StartupService.List(reg).Single(e => e.Name == "Discord").Enabled);
    True(eng.Restore(dis).Ok);
    True(StartupService.List(reg).Single(e => e.Name == "Discord").Enabled);
    True(reg.Read(RegHive.CurrentUser, StartupService.ApprovedPath, "Discord") is null, "approved marker removed because it did not exist before");
    True(reg.Read(RegHive.CurrentUser, StartupService.RunPath, "Discord") is not null, "the Run entry itself is never deleted");
});

// ------------------------------------------------------------------- games
Console.WriteLine("\nGame detection and launch");
Test("Steam: libraries, manifests, ignored runtimes, missing install dirs", () =>
{
    var root = Tmp();
    var steam = Path.Combine(root, "Steam"); var lib2 = Path.Combine(root, "Games", "SteamLibrary");
    Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Apex Legends"));
    Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "Rocket League"));
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
        "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib2.Replace("\\", "\\\\") + "\"\n\t}\n}");
    File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1172470.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1172470\"\n\t\"name\"\t\t\"Apex Legends\"\n\t\"installdir\"\t\t\"Apex Legends\"\n}");
    File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_252950.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"252950\"\n\t\"name\"\t\t\"Rocket League\"\n\t\"installdir\"\t\t\"Rocket League\"\n}");
    File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_228980.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"228980\"\n\t\"name\"\t\t\"Steamworks Common Redistributables\"\n\t\"installdir\"\t\t\"Steamworks Shared\"\n}");
    File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_999.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"999\"\n\t\"name\"\t\t\"Uninstalled Game\"\n\t\"installdir\"\t\t\"Gone\"\n}");
    var games = GameDetector.DetectSteam(steam).ToList();
    Eq(2, games.Count, string.Join(",", games.Select(g => g.Name)));
    True(games.Any(g => g.Id == "steam:252950"), "second library scanned");
});
Test("Epic: manifest parsed, incomplete installs ignored, token sanitised", () =>
{
    var root = Tmp(); var inst = Path.Combine(root, "Fortnite"); Directory.CreateDirectory(inst);
    var json = JsonSerializer.Serialize(new Dictionary<string, object> {
        ["DisplayName"] = "Fortnite", ["InstallLocation"] = inst, ["AppName"] = "Fortnite", ["CatalogNamespace"] = "fn", ["CatalogItemId"] = "4fe75bbc5a674f4f9b356b5c90567da5", ["LaunchExecutable"] = "FortniteGame\\Binaries\\Win64\\FortniteClient-Win64-Shipping.exe" });
    var g = GameDetector.ParseEpicManifest(json)!;
    Eq("epic:Fortnite", g.Id); Eq("fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite", g.LaunchToken);
    var psi = GameLauncher.BuildStartInfo(g);
    Eq("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true", psi.FileName);
    var inc = JsonSerializer.Serialize(new Dictionary<string, object> { ["DisplayName"] = "X", ["InstallLocation"] = inst, ["AppName"] = "X", ["bIsIncompleteInstall"] = true });
    True(GameDetector.ParseEpicManifest(inc) is null);
    var evil = JsonSerializer.Serialize(new Dictionary<string, object> { ["DisplayName"] = "Evil", ["InstallLocation"] = inst, ["AppName"] = "a&calc", ["CatalogNamespace"] = "n", ["CatalogItemId"] = "i" });
    True(GameDetector.ParseEpicManifest(evil)!.LaunchToken is null, "malicious token must be dropped");
});
Test("Riot: client + product metadata", () =>
{
    var root = Tmp(); var client = Path.Combine(root, "RiotClientServices.exe"); File.WriteAllText(client, "");
    var val = Path.Combine(root, "VALORANT"); Directory.CreateDirectory(val);
    File.WriteAllText(Path.Combine(root, "RiotClientInstalls.json"), JsonSerializer.Serialize(new { rc_default = client }));
    var meta = Path.Combine(root, "Metadata", "valorant.live"); Directory.CreateDirectory(meta);
    File.WriteAllText(Path.Combine(meta, "valorant.live.product_settings.yaml"), $"product_install_full_path: \"{val.Replace('\\', '/')}\"\n");
    var games = GameDetector.DetectRiot(Path.Combine(root, "RiotClientInstalls.json"), Path.Combine(root, "Metadata")).ToList();
    Eq(1, games.Count); Eq("VALORANT", games[0].Name);
    var psi = GameLauncher.BuildStartInfo(games[0]);
    True(psi.ArgumentList.Contains("--launch-product=valorant"));
});
Test("launch requests are validated", () =>
{
    var steam = new Game { Id = "steam:1", Name = "x", Launcher = GameLauncherKind.Steam, LaunchToken = "1172470" };
    Eq("steam://rungameid/1172470", GameLauncher.BuildStartInfo(steam).FileName);
    var bad = new Game { Id = "steam:2", Name = "x", Launcher = GameLauncherKind.Steam, LaunchToken = "1; calc" };
    try { GameLauncher.BuildStartInfo(bad); throw new Exception("accepted"); } catch (InvalidOperationException) { }
    var txt = Path.Combine(Tmp(), "a.txt"); File.WriteAllText(txt, "");
    try { GameLauncher.BuildStartInfo(new Game { Id = "m", Name = "m", Launcher = GameLauncherKind.Manual, ExePath = txt }); throw new Exception("accepted"); } catch (InvalidOperationException) { }
    try { GameLauncher.BuildStartInfo(new Game { Id = "m", Name = "m", Launcher = GameLauncherKind.Manual, ExePath = "/nope/missing.exe" }); throw new Exception("accepted"); } catch (FileNotFoundException) { }
});
Test("manual games appear only while the exe exists", () =>
{
    var exe = Path.Combine(Tmp(), "Game.exe"); File.WriteAllText(exe, "");
    var src = new GameSources(null, null, null, null);
    Eq(1, GameDetector.DetectAll(src, [new ManualGame { Name = "My Game", ExePath = exe }]).Count);
    File.Delete(exe);
    Eq(0, GameDetector.DetectAll(src, [new ManualGame { Name = "My Game", ExePath = exe }]).Count);
});

// --------------------------------------------------------------- game mode
Console.WriteLine("\nGame Mode");
Test("preset action counts are real, not marketing", () =>
{
    Eq(2, GameModePresets.Balanced.Count); Eq(5, GameModePresets.Performance.Count);
    var s = new AppSettings { CustomActions = ["WindowsGameMode", "Bogus", "RaiseGamePriority", "WindowsGameMode"] };
    Eq(2, GameModePresets.Custom(s).Count);
});
Test("session applies the preset and restores only what it changed", () =>
{
    var (eng, reg, pwr, _, _) = NewEngine();
    var state = Path.Combine(Tmp(), "session.json");
    // Notifications are ALREADY off before the session: must be left alone afterwards.
    reg.Seed(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", new RegValue(RegKind.DWord, 0));
    var sess = new GameModeSession(eng, new AppLog(null), state);
    var preset = new GameModePreset("t", "t", "t", [GameModeAction.WindowsGameMode, GameModeAction.MuteNotifications, GameModeAction.HighPerformancePlan]);
    var res = sess.Start("Test Game", preset, new AppSettings(), isLaptop: false, onBattery: false);
    True(res.All(r => r.Done), string.Join("; ", res.Select(r => r.Message)));
    Eq(OptState.Applied, eng.GetState(Catalog.Find("core.gamemode")!)); Eq(PowerGuids.HighPerformance, pwr.Active);
    True(File.Exists(state), "state persisted for crash recovery");
    sess.End();
    Eq(OptState.NotApplied, eng.GetState(Catalog.Find("core.gamemode")!)); Eq(PowerGuids.Balanced, pwr.Active);
    Eq(0, Convert.ToInt32(reg.Read(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled")!.Data), "pre-existing setting untouched");
    True(!File.Exists(state));
});
Test("high-performance plan is skipped on battery", () =>
{
    var (eng, _, pwr, _, _) = NewEngine();
    var sess = new GameModeSession(eng, new AppLog(null), Path.Combine(Tmp(), "s.json"));
    var res = sess.Start("g", new GameModePreset("t", "t", "t", [GameModeAction.HighPerformancePlan]), new AppSettings(), true, true);
    True(!res[0].Done); Eq(PowerGuids.Balanced, pwr.Active); sess.End();
});
Test("a crashed session is recovered on next start", () =>
{
    var (eng, reg, pwr, store, host) = NewEngine();
    var state = Path.Combine(Tmp(), "session.json");
    var s1 = new GameModeSession(eng, new AppLog(null), state);
    s1.Start("g", GameModePresets.Performance, new AppSettings(), false, false);
    Eq(PowerGuids.HighPerformance, pwr.Active);
    // simulate crash: new session object, same state file, same stores
    var s2 = new GameModeSession(eng, new AppLog(null), state);
    s2.RecoverIfPending();
    Eq(PowerGuids.Balanced, pwr.Active); Eq(OptState.NotApplied, eng.GetState(Catalog.Find("core.gamedvr")!));
});
Test("process cleaner never closes protected processes", () =>
{
    foreach (var n in new[] { "explorer", "csrss", "MsMpEng", "steam", "EasyAntiCheat", "vgc", "NoxTweak" }) True(ProcessCleaner.IsProtected(n), n);
    var report = ProcessCleaner.Close(["explorer", "lsass"], force: true);
    True(report.All(r => r.Contains("protected")), string.Join(";", report));
});

// ----------------------------------------------------------- recommendations
Console.WriteLine("\nRecommendations, AI gatekeeper, network");
SystemSnapshot Snap(long total = 16L << 30, long avail = 8L << 30, bool? laptop = false, string plan = PowerGuids.Balanced) => new()
{
    Os = Fact.Verified("Windows 11"), WindowsBuild = 22631, Ram = Fact.Verified($"{total >> 30} GB"), RamTotalBytes = total, RamAvailableBytes = avail,
    Cpu = Fact.Verified("Test CPU"), Gpus = [new GpuInfo("Test GPU", Fact.Verified("1.0"), Fact.Verified("8 GB"))],
    PowerPlan = Fact.Verified("Balanced"), PowerPlanGuid = plan, IsLaptop = laptop, IsAdmin = false,
    Drives = [new DriveInfoFact("C:\\", 500L << 30, 30L << 30, "NTFS", Fact.Unavailable("x"))],
};
Test("a configuration that is already optimal produces no tweak recommendations", () =>
{
    var (eng, reg, pwr, _, _) = NewEngine(admin: true);
    foreach (var d in Catalog.All.Where(d => d.Changes.Count > 0 && d.Id != "adv.bgapps")) eng.Apply(d);
    pwr.Active = PowerGuids.HighPerformance;
    var plan = RecommendationEngine.Build(Snap(plan: PowerGuids.HighPerformance), eng, 2);
    var tweaks = plan.Items.Where(i => i.Definition is not null && i.Group != PlanGroup.NotRecommended).ToList();
    Eq(0, tweaks.Count, string.Join(",", tweaks.Select(t => t.Title)));
});
Test("fresh PC gets evidence-backed recommendations", () =>
{
    var (eng, _, _, _, _) = NewEngine();
    var plan = RecommendationEngine.Build(Snap(), eng, 3);
    True(plan.Items.Any(i => i.Definition?.Id == "core.gamemode"));
    True(plan.Items.Any(i => i.Definition?.Id == "power.high"), "desktop on Balanced");
    True(plan.Applicable.All(i => i.Evidence.Length > 5 && i.Reason.Length > 10));
});
Test("laptop is not pushed towards High Performance", () =>
{
    var (eng, _, _, _, _) = NewEngine();
    var plan = RecommendationEngine.Build(Snap(laptop: true), eng, 3);
    True(!plan.Applicable.Any(i => i.Definition?.Id == "power.high"));
});
Test("low memory and low disk raise high-priority items; bottleneck stays a hypothesis", () =>
{
    var (eng, _, _, _, _) = NewEngine();
    var s = Snap(avail: 1L << 30);
    var plan = RecommendationEngine.Build(new SystemSnapshot { Os = s.Os, WindowsBuild = s.WindowsBuild, Ram = s.Ram, RamTotalBytes = 16L << 30, RamAvailableBytes = 1L << 30, PowerPlanGuid = s.PowerPlanGuid, PowerPlan = s.PowerPlan, IsLaptop = false,
        Drives = [new DriveInfoFact("C:\\", 100L << 30, 5L << 30, "NTFS", Fact.Unavailable("x"))] }, eng, 0);
    Eq(2, plan.Of(PlanGroup.HighPriority).Count);
    True(!plan.Items.Single(i => i.Title.StartsWith("CPU- or GPU-limited")).Confirmed);
});
Test("quick optimization set contains only low-risk, non-admin, safe-tier tweaks", () =>
{
    var (eng, _, _, _, _) = NewEngine();
    var plan = RecommendationEngine.Build(Snap(), eng, 3);
    foreach (var d in plan.SafeQuickSet) { Eq(Tier.Safe, d.Tier); Eq(RiskLevel.Low, d.Risk); True(!d.NeedsAdminRights); True(d.PowerPlanGuid is null); }
});
Test("plan validator rejects unknown, advanced and inapplicable proposals", () =>
{
    var (eng, _, _, _, _) = NewEngine(build: 19041);
    var v = PlanValidator.Validate(["core.gamemode", "reg add HKLM\\evil", "disable.defender", "adv.hags", "adv.bgapps"], eng, Tier.Safe);
    Eq(1, v.Accepted.Count); Eq(4, v.Rejected.Count);
    var v2 = PlanValidator.Validate(["adv.hags"], eng, Tier.Advanced);
    Eq(1, v2.Accepted.Count);
});
Test("AI is honestly reported as not configured", () =>
{
    var ai = new NoAiAdvisor(); Eq(AiStatus.NotConfigured, ai.Status);
    try { ai.ProposeAsync("x", default).GetAwaiter().GetResult(); throw new Exception("returned data"); } catch (InvalidOperationException) { }
});
Test("ping target validation", () =>
{
    True(NetworkDiagnostics.IsValidTarget("1.1.1.1")); True(NetworkDiagnostics.IsValidTarget("game-server.example.com"));
    True(!NetworkDiagnostics.IsValidTarget("1.1.1.1 && calc")); True(!NetworkDiagnostics.IsValidTarget("")); True(!NetworkDiagnostics.IsValidTarget("a;b"));
    var r = NetworkDiagnostics.PingAsync("bad host!", 3, null, default).GetAwaiter().GetResult();
    True(r.Error is not null && r.Sent == 0);
});
Test("scan on a non-Windows host reports Unsupported instead of inventing data", () =>
{
    if (OperatingSystem.IsWindows()) return;
    var s = SystemInventory.Scan(new FakePowerPlanService(), new FixedHostInfo(0, false));
    Eq(FactStatus.Unsupported, s.Os.Status); True(s.Gpus.Count == 0);
});
Test("settings round-trip and survive corruption", () =>
{
    var p = Path.Combine(Tmp(), "settings.json"); var st = new SettingsStore(p);
    var a = new AppSettings { PresetId = "performance", CloseList = ["discord"], AutomaticMode = true }; st.Save(a);
    var b = st.Load(); Eq("performance", b.PresetId); Eq("discord", b.CloseList[0]);
    File.WriteAllText(p, "{ not json"); Eq("balanced", st.Load().PresetId);
});

// --------------------------------------------------------------- view-models
Console.WriteLine("\nView-models (same logic the WPF screens bind to)");
(AppServices svc, FakeUi ui, InMemoryRegistry reg, FakePowerPlanService pwr, MainViewModel vm) NewApp(bool admin = false)
{
    var reg = new InMemoryRegistry(); var pwr = new FakePowerPlanService();
    var svc = new AppServices(new AppPaths { Root = Tmp() }, reg, pwr, new FixedHostInfo(22631, admin), new GameSources(null, null, null, null));
    var ui = new FakeUi();
    return (svc, ui, reg, pwr, new MainViewModel(svc, ui));
}
void Run(ICommand c, object? p = null) { if (c is AsyncCommand a) a.ExecuteAsync(p).GetAwaiter().GetResult(); else c.Execute(p); }

Test("navigation highlights the page and General:Apps selects the Apps tab", () =>
{
    var (_, _, _, _, vm) = NewApp();
    Eq(true, vm.GeneralNav.First(n => n.Key == "Home").IsOn);
    vm.Navigate("General:Apps");
    Eq(true, vm.MainNav.First(n => n.Key == "General").IsOn); Eq(false, vm.GeneralNav.First(n => n.Key == "Home").IsOn);
    True(ReferenceEquals(vm.Current, vm.General)); True(vm.General.Tabs.First(t => t.Tab == OptTab.Apps).IsOn);
});
Test("card toggle applies, mirrors real state and restores", () =>
{
    var (svc, ui, reg, _, vm) = NewApp();
    vm.General.LoadAsync().GetAwaiter().GetResult();
    var card = vm.General.Cards.First(c => c.Definition!.Id == "core.gamemode");
    True(!card.IsOn);
    Run(card.ToggleCommand);
    True(card.IsOn); Eq(OptState.Applied, svc.Engine.GetState(card.Definition!));
    Run(card.ToggleCommand);
    True(!card.IsOn); Eq(0, reg.Count);
});
Test("a failed write leaves the switch off and shows the failure", () =>
{
    var (svc, ui, reg, _, vm) = NewApp();
    reg.FailWritesTo(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
    vm.General.LoadAsync().GetAwaiter().GetResult();
    var card = vm.General.Cards.First(c => c.Definition!.Id == "core.transparency");
    Run(card.ToggleCommand);
    True(!card.IsOn, "switch must not pretend"); True(card.Status.StartsWith("Failed"), card.Status);
    True(ui.Infos.Count == 1, "user is told what happened");
});
Test("advanced change asks for confirmation and cancelling changes nothing", () =>
{
    var (svc, ui, reg, _, vm) = NewApp(admin: true);
    vm.Advanced.LoadAsync().GetAwaiter().GetResult();
    var card = vm.Advanced.Cards.First(c => c.Definition!.Id == "adv.hags");
    ui.ConfirmAnswer = false;
    Run(card.ToggleCommand);
    Eq(0, reg.Count); True(!card.IsOn); Eq(1, ui.Confirms.Count);
    ui.ConfirmAnswer = true;
    Run(card.ToggleCommand);
    True(card.IsOn); True(svc.Settings.PendingRestart.Any(p => p.Contains("restart")), "restart tracked, not performed");
    True(!ui.WindowsRestarted, "never restarts Windows automatically");
});
Test("admin-only change offers elevation instead of failing silently", () =>
{
    var (_, ui, reg, _, vm) = NewApp(admin: false);
    vm.Advanced.LoadAsync().GetAwaiter().GetResult();
    var card = vm.Advanced.Cards.First(c => c.Definition!.Id == "adv.hags");
    ui.ConfirmAnswer = true;
    Run(card.ToggleCommand);
    Eq(0, reg.Count); True(ui.ElevationRequested); True(!card.IsOn);
});
Test("search, filter and sort act on the real cards", () =>
{
    var (_, _, _, _, vm) = NewApp();
    vm.General.LoadAsync().GetAwaiter().GetResult();
    var all = vm.General.Cards.Count; True(all >= 4);
    vm.General.SearchText = "notification"; Eq(1, vm.General.Cards.Count);
    vm.General.SearchText = "zzzz"; True(vm.General.IsEmpty);
    vm.General.SearchText = "";
    vm.General.FilterOptions.First(o => o.Label == "With warnings").Command.Execute(null);
    True(vm.General.Cards.All(c => c.HasWarning) && vm.General.Cards.Count > 0);
    vm.General.FilterOptions.First(o => o.Label == "All").Command.Execute(null);
    Eq(all, vm.General.Cards.Count);
});
Test("Apps tab lists startup entries and toggling is reversible", () =>
{
    var (svc, _, reg, _, vm) = NewApp();
    reg.Seed(RegHive.CurrentUser, StartupService.RunPath, "Spotify", new RegValue(RegKind.String, @"C:\spotify.exe"));
    vm.General.SelectTab(OptTab.Apps);
    vm.General.LoadAsync().GetAwaiter().GetResult();
    var card = vm.General.Cards.Single(); True(card.IsOn);
    Run(card.ToggleCommand); True(!card.IsOn);
    Run(card.ToggleCommand); True(card.IsOn);
    True(reg.Read(RegHive.CurrentUser, StartupService.ApprovedPath, "Spotify") is null);
});
Test("power plan cards switch and restore the plan", () =>
{
    var (_, _, _, pwr, vm) = NewApp();
    vm.General.SelectTab(OptTab.Powerplan); vm.General.LoadAsync().GetAwaiter().GetResult();
    var high = vm.General.Cards.First(c => c.Definition!.Id == "power.high");
    Run(high.ToggleCommand); Eq(PowerGuids.HighPerformance, pwr.Active); True(high.IsOn);
    Run(high.ToggleCommand); Eq(PowerGuids.Balanced, pwr.Active);
});
Test("scan on this host shows only honest statuses (no invented specs)", () =>
{
    var (svc, _, _, _, vm) = NewApp();
    Run(vm.Home.ScanCommand);
    True(svc.Snapshot is not null); True(vm.Home.Specs.All(s => s.Status is "Verified" or "Unavailable" or "Unsupported" or "Estimated" or "Permission denied"));
    True(vm.Home.Specs.Count == 0 || vm.Home.Specs.All(s => s.Value.Length > 0));
});
Test("Optimize my PC: cancel changes nothing, confirm applies only the listed safe set", () =>
{
    var (svc, ui, reg, _, vm) = NewApp();
    ui.ConfirmAnswer = false; Run(vm.OptimizeCommand);
    Eq(0, reg.Count);
    var expected = svc.Plan!.SafeQuickSet.Select(d => d.Id).ToHashSet(); // the plan the user was shown
    True(expected.Count > 0, "scan produced a safe quick set");
    ui.ConfirmAnswer = true; Run(vm.OptimizeCommand);
    foreach (var id in expected) Eq(OptState.Applied, svc.Engine.GetState(Catalog.Find(id)!), id);
    Eq(expected.Count, svc.Backups.List().Count);
    True(!svc.Engine.GetState(Catalog.Find("adv.hags")!).Equals(OptState.Applied));
});
Test("manual game is added, listed and survives a restart of the settings", () =>
{
    var (svc, ui, _, _, vm) = NewApp();
    var exe = Path.Combine(Tmp(), "MyGame.exe"); File.WriteAllText(exe, "");
    ui.Picked = exe; Run(vm.Games.AddGameCommand);
    Eq(1, vm.Games.Games.Count); Eq("MANUAL", vm.Games.Games[0].Launcher);
    var again = new SettingsStore(svc.Paths.SettingsFile).Load();
    Eq(1, again.ManualGames.Count);
});
Test("failed launch with Game Mode on leaves no changes behind", () =>
{
    var (svc, ui, reg, pwr, vm) = NewApp();
    svc.Settings.GameModeActive = true; svc.Settings.PresetId = "performance";
    var bad = new Game { Id = "steam:bad", Name = "Bad", Launcher = GameLauncherKind.Steam, LaunchToken = "x;y" };
    try { svc.Controller.Launch(bad); throw new Exception("should have failed"); } catch (InvalidOperationException) { }
    Eq(PowerGuids.Balanced, pwr.Active);
    Eq(OptState.NotApplied, svc.Engine.GetState(Catalog.Find("core.gamemode")!));
    True(!svc.Session.IsActive);
});
Test("Game Mode page: presets, custom actions and process list persist", () =>
{
    var (svc, _, _, _, vm) = NewApp();
    vm.GameMode.ActionRows.First(r => r.Key == "MuteNotifications").ToggleCommand.Execute(null);
    Eq("custom", svc.Settings.PresetId); True(svc.Settings.CustomActions.Contains("MuteNotifications"));
    vm.GameMode.Presets.First(p => p.Id == "performance").SelectCommand.Execute(null);
    Eq("performance", svc.Settings.PresetId);
    Run(vm.GameMode.ToggleActiveCommand); True(svc.Settings.GameModeActive);
    Run(vm.GameMode.ToggleForceCommand); True(svc.Settings.ForceClose);
});
Test("ping UI refuses an invalid target without touching the network", () =>
{
    var (_, _, _, _, vm) = NewApp();
    vm.Network.Target = "bad target; calc"; Run(vm.Network.PingCommand);
    True(vm.Network.ResultText.Contains("valid"));
});
Test("backups page lists restorable changes and restores through the engine", () =>
{
    var (svc, ui, reg, _, vm) = NewApp();
    svc.Engine.Apply(Catalog.Find("priv.adid")!);
    vm.Backups.Load(); Eq(1, vm.Backups.Rows.Count); True(vm.Backups.Rows[0].CanRestore);
    ui.ConfirmAnswer = true; Run(vm.Backups.Rows[0].RestoreCommand);
    Eq(0, reg.Count); vm.Backups.Load(); True(!vm.Backups.Rows[0].CanRestore, "restored backups cannot be reused");
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeUi : IUi
{
    public bool ConfirmAnswer = true; public string? Picked;
    public List<string> Confirms = new(), Infos = new(), Toasts = new();
    public bool ElevationRequested, WindowsRestarted;
    public Task<bool> ConfirmAsync(string title, string message, string primary, string? secondary = "Cancel", IReadOnlyList<string>? details = null)
    { Confirms.Add(title); return Task.FromResult(ConfirmAnswer); }
    public Task InfoAsync(string title, string message, IReadOnlyList<string>? details = null) { Infos.Add(title + ": " + message); return Task.CompletedTask; }
    public string? PickExecutable() => Picked;
    public void OpenFolder(string path) { }
    public void RestartAsAdministrator() => ElevationRequested = true;
    public void RestartWindows() => WindowsRestarted = true;
    public IDisposable StartTimer(TimeSpan interval, Action tick) => new Noop();
    public void Post(Action action) => action();
    public void Toast(string text) => Toasts.Add(text);
    sealed class Noop : IDisposable { public void Dispose() { } }
}
