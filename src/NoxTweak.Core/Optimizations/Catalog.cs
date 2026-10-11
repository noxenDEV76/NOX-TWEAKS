using NoxTweak.Core.Model;

namespace NoxTweak.Core.Optimizations;

public static class PowerGuids
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public static readonly string[] Allowed = [Balanced, HighPerformance, Ultimate];
}

/// <summary>
/// The complete allow-list of optimizations NOX TWEAK can perform. Each entry is a documented,
/// narrowly scoped change that can be restored to the exact original state.
/// </summary>
public static class Catalog
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    private static RegistryChange Cu(string path, string name, int v) => new(RegHive.CurrentUser, path, name, RegKind.DWord, v);
    private static RegistryChange CuS(string path, string name, string v) => new(RegHive.CurrentUser, path, name, RegKind.String, v);

    public static readonly IReadOnlyList<OptimizationDefinition> All = Build();

    public static OptimizationDefinition? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>Things other optimizers offer that NOX TWEAK deliberately refuses, with the reason.</summary>
    public static readonly IReadOnlyList<(string Title, string Reason)> Refused =
    [
        ("Disable Windows Defender / Firewall / security mitigations", "Removes protection for a gain that has never been shown to be meaningful in games."),
        ("Disable Windows Update or driver searching", "Leaves the PC without security fixes and can break driver delivery. Pause updates from Windows itself if needed."),
        ("Disable Search Indexing or Scheduled Maintenance for everyone", "Hurts everyday use and the benefit while gaming is negligible on modern PCs."),
        ("Remove Microsoft Edge / Store components", "Can break Windows features and apps that depend on WebView2 and the Store."),
        ("Disable Bluetooth or other services in bulk", "Breaks headsets, controllers and peripherals; the CPU time saved is not measurable."),
        ("Core parking, TCP/Nagle and 'network throttling' registry tweaks", "Copied from videos without evidence on modern Windows; may reduce stability or efficiency."),
        ("Standby-list / RAM cleaners running in the background", "Forces Windows to reload data from disk and usually lowers performance."),
    ];

    private static IReadOnlyList<OptimizationDefinition> Build() =>
    [
        // ------------------------------------------------------------------ CORE
        new()
        {
            Id = "core.gamemode", Title = "Enable Windows Game Mode",
            Description = "Lets Windows give the game you are playing priority over background tasks such as Windows Update installs.",
            Technical = "Sets AutoGameModeEnabled and AllowAutoGameMode to 1 under HKCU\\Software\\Microsoft\\GameBar. This is the same switch as Settings > Gaming > Game Mode.",
            Justification = "Documented Windows feature (Microsoft Support: 'Game Mode in Windows'). Effect varies by game; no FPS gain is promised.",
            Tab = OptTab.Core, MinBuild = 15063,
            Changes =
            [
                Cu(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
                Cu(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
            ],
        },
        new()
        {
            Id = "core.gamedvr", Title = "Disable Game DVR Background Recording",
            Description = "Stops Windows from recording gameplay in the background, which uses GPU encoding and disk writes.",
            Technical = "Sets GameDVR_Enabled=0 (HKCU\\System\\GameConfigStore) and AppCaptureEnabled=0 (HKCU\\...\\GameDVR).",
            Justification = "Background capture runs a hardware encoder and writes to disk while you play. Turning it off is a documented user setting in Xbox Game Bar.",
            Warning = "Win+Alt+G background clips and 'Record that' will stop working.",
            Tab = OptTab.Core, MinBuild = 10586,
            Changes =
            [
                Cu(@"System\GameConfigStore", "GameDVR_Enabled", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
            ],
        },
        new()
        {
            Id = "core.notifications", Title = "Turn Off Toast Notifications",
            Description = "Hides pop-up notifications from apps and Windows so nothing interrupts you. This is about focus, not FPS.",
            Technical = "Sets ToastEnabled=0 under HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications.",
            Justification = "Equivalent to turning off 'Get notifications from apps and other senders' in Settings > Notifications.",
            Warning = "You will not see alerts from any app until you restore this.",
            Tab = OptTab.Core, MinBuild = 10240,
            Changes = [Cu(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0)],
        },
        new()
        {
            Id = "core.animations", Title = "Disable Window Animations",
            Description = "Turns off minimize/maximize and taskbar animations for a snappier desktop.",
            Technical = "Sets MinAnimate=\"0\" (HKCU\\Control Panel\\Desktop\\WindowMetrics) and TaskbarAnimations=0 (HKCU\\...\\Explorer\\Advanced).",
            Justification = "Documented Performance Options > 'Animate windows when minimizing and maximizing'.",
            Restart = RestartNeed.SignOut, Tab = OptTab.Core,
            Changes =
            [
                CuS(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0),
            ],
        },
        new()
        {
            Id = "core.transparency", Title = "Disable Transparency Effects",
            Description = "Removes the blur/see-through effect from Start, taskbar and windows. Slightly lighter on weak GPUs.",
            Technical = "Sets EnableTransparency=0 under HKCU\\...\\Themes\\Personalize (Settings > Personalization > Colors).",
            Justification = "Documented personalization setting; benefit is small and mostly visible on integrated graphics.",
            Tab = OptTab.Core, MinBuild = 10240,
            Changes = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
        },

        // --------------------------------------------------------------- PRIVACY
        new()
        {
            Id = "priv.adid", Title = "Disable Advertising ID",
            Description = "Stops apps from using a unique ID to show you personalised ads.",
            Technical = "Sets Enabled=0 under HKCU\\...\\AdvertisingInfo.",
            Justification = "Documented privacy setting (Settings > Privacy > General).",
            Tab = OptTab.Privacy,
            Changes = [Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)],
        },
        new()
        {
            Id = "priv.tips", Title = "Disable Tips & Start Suggestions",
            Description = "Turns off tips, suggested apps and promotional content in Windows.",
            Technical = $"Sets SubscribedContent-338388Enabled, SubscribedContent-338389Enabled, SystemPaneSuggestionsEnabled and SoftLandingEnabled to 0 under HKCU\\{Cdm}.",
            Justification = "Same switches as Settings > Personalization > Start / Notifications > 'Show suggestions'.",
            Tab = OptTab.Privacy,
            Changes =
            [
                Cu(Cdm, "SubscribedContent-338388Enabled", 0),
                Cu(Cdm, "SubscribedContent-338389Enabled", 0),
                Cu(Cdm, "SystemPaneSuggestionsEnabled", 0),
                Cu(Cdm, "SoftLandingEnabled", 0),
            ],
        },
        new()
        {
            Id = "priv.tailored", Title = "Disable Tailored Experiences",
            Description = "Prevents Windows from using diagnostic data to personalise tips and recommendations.",
            Technical = "Sets TailoredExperiencesWithDiagnosticDataEnabled=0 under HKCU\\...\\Privacy.",
            Justification = "Documented privacy setting (Settings > Privacy > Diagnostics & feedback).",
            Tab = OptTab.Privacy,
            Changes = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)],
        },
        new()
        {
            Id = "priv.websearch", Title = "Disable Web Results in Start Search",
            Description = "Start search only looks at your PC instead of also sending queries to Bing.",
            Technical = "Sets DisableSearchBoxSuggestions=1 under HKCU\\Software\\Policies\\Microsoft\\Windows\\Explorer (documented Group Policy value).",
            Justification = "Documented policy 'Turn off display of recent search entries / web suggestions in the search box'.",
            Warning = "Policy-based; some managed PCs may override it.",
            Tab = OptTab.Privacy, MinBuild = 10240,
            Changes = [Cu(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)],
        },

        // ------------------------------------------------------------------- QOL
        new()
        {
            Id = "qol.mouseaccel", Title = "Disable Pointer Acceleration",
            Description = "Makes mouse movement 1:1 with your hand (Windows 'Enhance pointer precision' off). Popular for aiming consistency.",
            Technical = "Sets MouseSpeed, MouseThreshold1 and MouseThreshold2 to \"0\" under HKCU\\Control Panel\\Mouse.",
            Justification = "Documented Mouse Properties setting. It changes feel, not FPS.",
            Warning = "Your mouse will feel different. Sign out and back in to apply.",
            Restart = RestartNeed.SignOut, Tab = OptTab.Qol,
            Changes =
            [
                CuS(@"Control Panel\Mouse", "MouseSpeed", "0"),
                CuS(@"Control Panel\Mouse", "MouseThreshold1", "0"),
                CuS(@"Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
        },
        new()
        {
            Id = "qol.stickykeys", Title = "Disable Sticky/Filter Keys Shortcuts",
            Description = "Stops the Shift-x5 and hold-Shift pop-ups that can minimize a game.",
            Technical = "Sets Flags to 506 (StickyKeys), 122 (Keyboard Response) and 58 (ToggleKeys) under HKCU\\Control Panel\\Accessibility, which disables only the keyboard shortcut prompts.",
            Justification = "Documented accessibility shortcut flags.",
            Warning = "Do not use this if you rely on Sticky Keys for accessibility.",
            Tab = OptTab.Qol,
            Changes =
            [
                CuS(@"Control Panel\Accessibility\StickyKeys", "Flags", "506"),
                CuS(@"Control Panel\Accessibility\Keyboard Response", "Flags", "122"),
                CuS(@"Control Panel\Accessibility\ToggleKeys", "Flags", "58"),
            ],
        },
        new()
        {
            Id = "qol.fileext", Title = "Show File Extensions",
            Description = "Always show .exe, .zip, .png… so you can spot disguised files.",
            Technical = "Sets HideFileExt=0 under HKCU\\...\\Explorer\\Advanced.",
            Justification = "Documented Folder Options setting; also a basic safety habit.",
            Tab = OptTab.Qol,
            Changes = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0)],
        },
        new()
        {
            Id = "qol.menudelay", Title = "Faster Menu Response",
            Description = "Removes the short delay before cascading menus open.",
            Technical = "Sets MenuShowDelay=\"0\" under HKCU\\Control Panel\\Desktop (default 400 ms).",
            Justification = "Documented desktop parameter; pure responsiveness change.",
            Restart = RestartNeed.SignOut, Tab = OptTab.Qol,
            Changes = [CuS(@"Control Panel\Desktop", "MenuShowDelay", "0")],
        },

        // ------------------------------------------------------------- POWER PLAN
        new()
        {
            Id = "power.balanced", Title = "Balanced Power Plan",
            Description = "Windows default: scales CPU speed with load. Best for laptops and everyday use.",
            Technical = $"Activates power scheme {PowerGuids.Balanced} with powercfg /setactive.",
            Justification = "Default Windows scheme.",
            Tab = OptTab.Powerplan, PowerPlanGuid = PowerGuids.Balanced,
        },
        new()
        {
            Id = "power.high", Title = "High Performance Power Plan",
            Description = "Keeps the CPU responsive instead of ramping up on demand. Can reduce micro-stutter in some games.",
            Technical = $"Activates power scheme {PowerGuids.HighPerformance} with powercfg /setactive.",
            Justification = "Built-in Windows scheme. Trade-off: higher idle power and heat.",
            Warning = "Uses more power and produces more heat. Not recommended on battery.",
            Risk = RiskLevel.Low, Tab = OptTab.Powerplan, PowerPlanGuid = PowerGuids.HighPerformance,
        },
        new()
        {
            Id = "power.ultimate", Title = "Ultimate Performance Power Plan",
            Description = "Windows' most aggressive built-in plan. Mostly useful on workstations and desktops.",
            Technical = $"Adds (if missing) and activates scheme {PowerGuids.Ultimate} with powercfg.",
            Justification = "Built-in Windows scheme for high-end PCs. Gains over High Performance are usually negligible for gaming.",
            Warning = "Highest power use and heat. Desktop on AC power only.",
            Risk = RiskLevel.Medium, Tier = Tier.Advanced, Tab = OptTab.Powerplan, MinBuild = 17134,
            PowerPlanGuid = PowerGuids.Ultimate,
        },

        // --------------------------------------------------------------- ADVANCED
        new()
        {
            Id = "adv.hags", Title = "Hardware-Accelerated GPU Scheduling",
            Description = "Lets the graphics card manage its own memory scheduling. Helps some games, hurts others - test it.",
            Technical = "Sets HwSchMode=2 under HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers. Requires a supported GPU and driver and a restart.",
            Justification = "Documented by Microsoft (DirectX blog, Windows 10 2004). Results vary by GPU, driver and game.",
            Warning = "Benefit is uncertain; some systems see stutter. Restart required.",
            Page = OptPage.Advanced, Tier = Tier.Advanced, Risk = RiskLevel.Medium, RequiresAdmin = true,
            Restart = RestartNeed.Required, MinBuild = 19041,
            Changes = [new(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegKind.DWord, 2)],
        },
        new()
        {
            Id = "adv.bgapps", Title = "Disable Background Apps (Windows 10)",
            Description = "Stops Store apps from running in the background when you are not using them.",
            Technical = "Sets GlobalUserDisabled=1 under HKCU\\...\\BackgroundAccessApplications. Honoured on Windows 10; Windows 11 uses per-app settings.",
            Justification = "Documented Windows 10 privacy setting. Not applicable to Windows 11.",
            Warning = "Mail, Calendar and similar apps will not update until opened.",
            Page = OptPage.Advanced, Tier = Tier.Advanced, MaxBuild = 19045,
            Changes = [Cu(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)],
        },
    ];
}
