﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Schneegans.Unattend;

public enum Pass
{
  offlineServicing,
  windowsPE,
  generalize,
  specialize,
  auditSystem,
  auditUser,
  oobeSystem
}

public enum RecoveryMode
{
  None, Folder, Partition
}

public enum PartitionLayout
{
  MBR, GPT
}

public enum ProcessorArchitecture
{
  x86, amd64, arm64
}

public enum ExpressSettingsMode
{
  Interactive, EnableAll, DisableAll
}

public enum TaskbarSearchMode
{
  Hide = 0,
  Icon = 1,
  Box = 2
}

abstract class CommandConfig
{
  public readonly static CommandConfig WindowsPE = new WindowsPECommandConfig();
  public readonly static CommandConfig Specialize = new SpecializeCommandConfig();
  public readonly static CommandConfig Oobe = new OobeCommandConfig();

  public abstract XmlElement CreateElement(XmlDocument doc, XmlNamespaceManager ns);

  /// <summary>
  /// Inserts a command using this markup:
  /// <code>
  /// &lt;settings pass=&quot;windowsPE&quot;&gt;
  ///   &lt;component name=&quot;Microsoft-Windows-Setup&quot;&gt;
  ///     &lt;RunSynchronous&gt;
  ///       &lt;RunSynchronousCommand&gt;
  ///         &lt;Path&gt;
  /// </code>
  /// </summary>
  private class WindowsPECommandConfig : CommandConfig
  {
    public override XmlElement CreateElement(XmlDocument doc, XmlNamespaceManager ns)
    {
      var container = Util.GetOrCreateElement(Pass.windowsPE, "Microsoft-Windows-Setup", "RunSynchronous", doc, ns);
      var outer = Util.NewElement("RunSynchronousCommand", container, doc, ns);
      return Util.NewElement("Path", outer, doc, ns);
    }
  }

  /// <summary>
  /// Inserts a command using this markup:
  /// <code>
  /// &lt;settings pass=&quot;specialize&quot;&gt;
  ///   &lt;component name=&quot;Microsoft-Windows-Deployment&quot;&gt;
  ///     &lt;RunSynchronous&gt;
  ///       &lt;RunSynchronousCommand&gt;
  ///         &lt;Path&gt;
  /// </code>
  /// </summary>
  private class SpecializeCommandConfig : CommandConfig
  {
    public override XmlElement CreateElement(XmlDocument doc, XmlNamespaceManager ns)
    {
      var container = Util.GetOrCreateElement(Pass.specialize, "Microsoft-Windows-Deployment", "RunSynchronous", doc, ns);
      var outer = Util.NewElement("RunSynchronousCommand", container, doc, ns);
      return Util.NewElement("Path", outer, doc, ns);
    }
  }

  /// <summary>
  /// Inserts a command using this markup:
  /// <code>
  /// &lt;settings pass=&quot;oobeSystem&quot;&gt;
  ///   &lt;component name=&quot;Microsoft-Windows-Shell-Setup&quot;&gt;
  ///     &lt;FirstLogonCommands&gt;
  ///       &lt;SynchronousCommand&gt;
  ///         &lt;CommandLine&gt;
  /// </code>
  /// </summary>
  private class OobeCommandConfig : CommandConfig
  {
    public override XmlElement CreateElement(XmlDocument doc, XmlNamespaceManager ns)
    {
      var container = Util.GetOrCreateElement(Pass.oobeSystem, "Microsoft-Windows-Shell-Setup", "FirstLogonCommands", doc, ns);
      var outer = Util.NewElement("SynchronousCommand", container, doc, ns);
      return Util.NewElement("CommandLine", outer, doc, ns);
    }
  }
}

class CommandAppender(XmlDocument doc, XmlNamespaceManager ns, CommandConfig config)
{
  public void Append(string value)
  {
    config.CreateElement(doc, ns).InnerText = value;
  }

  public void Append(IEnumerable<string> values)
  {
    foreach (string value in values)
    {
      Append(value);
    }
  }
}

static class CommandBuilder
{
  public static string Raw(string command)
  {
    return command;
  }

  public static string ShellCommand(string command)
  {
    return $@"cmd.exe /c ""{command}""";
  }

  /// <summary>
  /// Runs a command and redirects its <c>stdout</c> and <c>stderr</c> to a file.
  /// </summary>
  public static string ShellCommand(string command, string outFile)
  {
    return $@"cmd.exe /c ""{command} >>""{outFile}"" 2>&1""";
  }

  public static string RegistryCommand(string value)
  {
    return $"reg.exe {value}";
  }

  public delegate IEnumerable<string> RegistryDefaultUserAction(string rootKey, string subKey);

  public static IEnumerable<string> RegistryDefaultUserCommand(RegistryDefaultUserAction action)
  {
    string rootKey = "HKU";
    string subKey = "DefaultUser";

    IEnumerable<string> content = action.Invoke(rootKey, subKey);

    if (content.Any())
    {
      return [
        RegistryCommand(@$"load ""{rootKey}\{subKey}"" ""C:\Users\Default\NTUSER.DAT"""),
        .. content,
        RegistryCommand(@$"unload ""{rootKey}\{subKey}"""),
      ];
    }
    else
    {
      return [];
    }
  }

  public static string PowerShellCommand(string value)
  {
    {
      const char quote = '"';
      if (value.Contains(quote))
      {
        throw new ArgumentException($"PowerShell command '{value}' must not contain '{quote}'.");
      }
    }
    {
      const char semicolon = ';';
      const char brace = '}';
      if (!value.EndsWith(semicolon) && !value.EndsWith(brace))
      {
        throw new ArgumentException($"PowerShell command '{value}' must end with either '{semicolon}' or '{brace}'.");
      }
    }
    return @$"powershell.exe -NoProfile -Command ""{value}""";
  }

  public static string InvokePowerShellScript(string filepath)
  {
    return PowerShellCommand($"Get-Content -LiteralPath '{filepath}' -Raw | Invoke-Expression;");
  }

  public static string InvokeVBScript(string filepath)
  {
    return @$"cscript.exe //E:vbscript ""{filepath}""";
  }

  public static string InvokeJScript(string filepath)
  {
    return @$"cscript.exe //E:jscript ""{filepath}""";
  }

  public static IEnumerable<string> WriteToFile(string path, string line)
  {
    static string EscapeShell(string command)
    {
      return command
        .Replace("^", "^^")
        .Replace("&", "^&")
        .Replace("<", "^<")
        .Replace(">", "^>")
        .Replace("|", "^|")
        .Replace("%", "^%");
    }

    if (string.IsNullOrWhiteSpace(line))
    {
      yield break;
    }

    string command = $@"cmd.exe /c "">>""{path}"" echo {EscapeShell(line)}""";
    if (command.Length >= 255)
    {
      throw new ConfigurationException($"Line '{line}' is too long.");
    }
    yield return command;
  }
}

public class ConfigurationException(string? message) : Exception(message);

public record class Configuration(
  ILanguageSettings LanguageSettings,
  IAccountSettings AccountSettings,
  IPartitionSettings PartitionSettings,
  IEditionSettings EditionSettings,
  ILockoutSettings LockoutSettings,
  IPasswordExpirationSettings PasswordExpirationSettings,
  IProcessAuditSettings ProcessAuditSettings,
  IComputerNameSettings ComputerNameSettings,
  ITimeZoneSettings TimeZoneSettings,
  IWifiSettings WifiSettings,
  IWdacSettings WdacSettings,
  ImmutableHashSet<ProcessorArchitecture> ProcessorArchitectures,
  ImmutableDictionary<string, ImmutableSortedSet<Pass>> Components,
  ImmutableList<Bloatware> Bloatwares,
  ExpressSettingsMode ExpressSettings,
  ScriptSettings ScriptSettings,
  IKeySettings KeySettings,
  IWallpaperSettings WallpaperSettings,
  IColorSettings ColorSettings,
  bool BypassRequirementsCheck,
  bool BypassNetworkCheck,
  bool EnableLongPaths,
  bool EnableRemoteDesktop,
  bool HardenSystemDriveAcl,
  bool AllowPowerShellScripts,
  bool DisableLastAccess,
  bool NoAutoRebootWithLoggedOnUsers,
  bool DisableDefender,
  bool DisableSac,
  bool DisableUac,
  bool DisableSmartScreen,
  bool DisableSystemRestore,
  bool DisableFastStartup,
  bool TurnOffSystemSounds,
  bool DisableAppSuggestions,
  bool DisableWidgets,
  bool VBoxGuestAdditions,
  bool VMwareTools,
  bool VirtIoGuestTools,
  bool PreventDeviceEncryption,
  bool ClassicContextMenu,
  bool LeftTaskbar,
  bool DeleteTaskbarIcons,
  bool HideTaskViewButton,
  bool ShowFileExtensions,
  bool ShowAllTrayIcons,
  HideModes HideFiles,
  bool HideEdgeFre,
  bool MakeEdgeUninstallable,
  bool LaunchToThisPC,
  bool DisableWindowsUpdate,
  TaskbarSearchMode TaskbarSearch,
  IStartPinsSettings StartPinsSettings,
  IStartTilesSettings StartTilesSettings,
  CompactOsModes CompactOsMode
)
{
  public static Configuration Default => new(
    LanguageSettings: new InteractiveLanguageSettings(),
    AccountSettings: new InteractiveMicrosoftAccountSettings(),
    PartitionSettings: new InteractivePartitionSettings(),
    EditionSettings: new InteractiveEditionSettings(),
    LockoutSettings: new DefaultLockoutSettings(),
    PasswordExpirationSettings: new DefaultPasswordExpirationSettings(),
    ProcessAuditSettings: new DisabledProcessAuditSettings(),
    ComputerNameSettings: new RandomComputerNameSettings(),
    TimeZoneSettings: new ImplicitTimeZoneSettings(),
    WifiSettings: new InteractiveWifiSettings(),
    WdacSettings: new SkipWdacSettings(),
    ProcessorArchitectures: [ProcessorArchitecture.amd64],
    Components: ImmutableDictionary.Create<string, ImmutableSortedSet<Pass>>(),
    Bloatwares: [],
    ExpressSettings: ExpressSettingsMode.DisableAll,
    ScriptSettings: new ScriptSettings([]),
    KeySettings: new SkipKeySettings(),
    WallpaperSettings: new DefaultWallpaperSettings(),
    ColorSettings: new DefaultColorSettings(),
    BypassRequirementsCheck: false,
    BypassNetworkCheck: false,
    EnableLongPaths: false,
    EnableRemoteDesktop: false,
    HardenSystemDriveAcl: false,
    AllowPowerShellScripts: false,
    DisableLastAccess: false,
    NoAutoRebootWithLoggedOnUsers: false,
    DisableDefender: false,
    DisableSac: false,
    DisableUac: false,
    DisableSmartScreen: false,
    DisableFastStartup: false,
    DisableSystemRestore: false,
    TurnOffSystemSounds: false,
    DisableAppSuggestions: false,
    DisableWidgets: false,
    VBoxGuestAdditions: false,
    VMwareTools: false,
    VirtIoGuestTools: false,
    PreventDeviceEncryption: false,
    ClassicContextMenu: false,
    LeftTaskbar: false,
    DeleteTaskbarIcons: false,
    HideTaskViewButton: false,
    ShowFileExtensions: false,
    ShowAllTrayIcons: false,
    HideFiles: HideModes.Hidden,
    HideEdgeFre: false,
    MakeEdgeUninstallable: false,
    LaunchToThisPC: false,
    DisableWindowsUpdate: false,
    TaskbarSearch: TaskbarSearchMode.Box,
    StartPinsSettings: new DefaultStartPinsSettings(),
    StartTilesSettings: new DefaultStartTilesSettings(),
    CompactOsMode: CompactOsModes.Default
  );
}

/// <summary>
/// Collects PowerShell commands that will be run whenever a user logs on for the first time.
/// </summary>
public class UserOnceScript
{
  private bool needsExplorerRestart = false;
  private readonly List<string> commands = [];

  public void Append(string command)
  {
    commands.Add(command);
  }

  public void InvokeFile(string file)
  {
    Append($"Get-Content -LiteralPath '{file}' -Raw | Invoke-Expression;");
  }

  public void RestartExplorer()
  {
    needsExplorerRestart = true;
  }

  public string GetScript()
  {
    IEnumerable<string> Lines()
    {
      yield return "& {";
      foreach (string command in commands)
      {
        yield return command;
      }
      if (needsExplorerRestart)
      {
        yield return Util.StringFromResource("RestartExplorer.ps1");
      }
      yield return @"} *>&1 >> ""$env:TEMP\UserOnce.log"";";
    }
    return string.Join("\r\n", Lines());
  }
}

public interface IKeyed
{
  string Id { get; }
}

public abstract class BloatwareStep(
  string[] appliesTo
)
{
  public ImmutableSortedSet<string> AppliesTo { get; } = ImmutableSortedSet.CreateRange(appliesTo);
}

public class SelectorBloatwareStep(
  string[] appliesTo,
  string selector
) : BloatwareStep(appliesTo)
{
  public string Selector { get; } = selector;
}

public class PackageBloatwareStep(
  string[] appliesTo,
  string selector
) : SelectorBloatwareStep(appliesTo, selector);

public class CapabilityBloatwareStep(
  string[] appliesTo,
  string selector
) : SelectorBloatwareStep(appliesTo, selector);

public class OptionalFeatureBloatwareStep(
  string[] appliesTo,
  string selector
) : SelectorBloatwareStep(appliesTo, selector);

public class CustomBloatwareStep(
  string[] appliesTo
) : BloatwareStep(appliesTo);

public class Bloatware(
  string displayName,
  string? token,
  ImmutableList<BloatwareStep> steps
) : IKeyed
{
  public string DisplayName { get; } = displayName;

  public string Id { get; } = $"Remove{token ?? displayName.Replace(" ", "")}";

  public ImmutableList<BloatwareStep> Steps { get; } = steps;

  [JsonIgnore]
  public string? StepsLabel
  {
    get
    {
      IEnumerable<string> strings = Steps.SelectMany<BloatwareStep, string>(s => s switch
        {
          SelectorBloatwareStep sel => [sel.Selector],
          CustomBloatwareStep => [],
          _ => throw new NotImplementedException(),
        }
      );
      return strings.Any() ? string.Join(", ", strings) : null;
    }
  }
}

public class Component(
  string id,
  ImmutableSortedSet<Pass> passes
) : IKeyed
{
  public string Id { get; } = Validate(id);

  private static readonly Regex Pattern = new("^[a-z-]+$", RegexOptions.IgnoreCase);

  private static string Validate(string id)
  {
    if (!Pattern.IsMatch(id))
    {
      throw new ArgumentException($"ID '{id}' contains illegal characters.", nameof(id));
    }
    return id;
  }

  public ImmutableSortedSet<Pass> Passes { get; } = passes;

  public string Uri
  {
    get
    {
      string name = Id switch
      {
        "Microsoft-Windows-WDF-KernelLibrary" => "microsoft-windows-wdf-kernel-library",
        "Microsoft-Windows-MapControl-Desktop" => "microsoft-windows-mapcontrol",
        _ => Id.ToLowerInvariant(),
      };
      return $"https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/{name}";
    }
  }
}

public class WindowsEdition(
  string id,
  string displayName,
  string productKey,
  bool visible
) : IKeyed
{
  public string Id { get; } = id;

  public string DisplayName { get; } = displayName;

  public string ProductKey { get; } = productKey;

  public bool Visible = visible;
}

public class ImageLanguage(
  string id,
  string displayName
) : IKeyed
{
  public string Id { get; } = id;

  public string DisplayName { get; } = displayName;
}

public enum InputType
{
  Keyboard, IME
}

public class KeyboardIdentifier(
  string id,
  string displayName,
  InputType type
) : IKeyed
{
  public string Id { get; } = id;

  public string DisplayName { get; } = displayName;

  public InputType Type { get; } = type;
}

public class TimeOffset(
  string id,
  string displayName
) : IKeyed
{
  public string Id { get; } = id;

  public string DisplayName { get; } = displayName;
}

public class GeoLocation(
  string id,
  string displayName
) : IKeyed
{
  public string Id { get; } = id;

  public string DisplayName { get; } = displayName;
}

public class KeyboardConverter(
  UnattendGenerator generator
) : JsonConverter<KeyboardIdentifier>
{
  public override bool CanWrite => false;

  public override KeyboardIdentifier? ReadJson(JsonReader reader, Type objectType, KeyboardIdentifier? existingValue, bool hasExistingValue, JsonSerializer serializer)
  {
    return reader.TokenType switch
    {
      JsonToken.String => generator.Lookup<KeyboardIdentifier>("" + reader.Value),
      JsonToken.Null => null,
      _ => throw new NotSupportedException(),
    };
  }

  public override void WriteJson(JsonWriter writer, KeyboardIdentifier? value, JsonSerializer serializer)
  {
    throw new NotImplementedException();
  }
}

public class GeoLocationConverter(
  UnattendGenerator generator
) : JsonConverter<GeoLocation>
{
  public override bool CanWrite => false;

  public override GeoLocation? ReadJson(JsonReader reader, Type objectType, GeoLocation? existingValue, bool hasExistingValue, JsonSerializer serializer)
  {
    return reader.TokenType switch
    {
      JsonToken.String => generator.Lookup<GeoLocation>("" + reader.Value),
      JsonToken.Null => null,
      _ => throw new NotSupportedException(),
    };
  }

  public override void WriteJson(JsonWriter writer, GeoLocation? value, JsonSerializer serializer)
  {
    throw new NotImplementedException();
  }
}

public class UserLocale(
  string id,
  string displayName,
  KeyboardIdentifier? keyboardLayout,
  string lcid,
  GeoLocation? geoLocation
) : IKeyed
{
  public string Id { get; } = id;

  public string DisplayName { get; } = displayName;

  public string LCID { get; } = lcid;

  public KeyboardIdentifier? KeyboardLayout { get; } = keyboardLayout;

  public GeoLocation? GeoLocation { get; } = geoLocation;
}

public static class Constants
{
  public const string UsersGroup = "Users";

  public const string AdministratorsGroup = "Administrators";

  public const string DefaultPassword = "password";

  public const int RecoveryPartitionSize = 1000;

  public const int EspDefaultSize = 300;

  public static readonly string DiskpartScript = DiskModifier.GetCustomDiskpartScript();

  public const string MyNamespaceUri = "https://schneegans.de/windows/unattend-generator/";
}

public class UnattendGenerator
{
  public UnattendGenerator()
  {
    {
      string json = Util.StringFromResource("Bloatware.json");
      JsonSerializerSettings settings = new()
      {
        TypeNameHandling = TypeNameHandling.Auto,
      };
      Bloatwares = JsonConvert.DeserializeObject<Bloatware[]>(json, settings).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("Component.json");
      Components = JsonConvert.DeserializeObject<Component[]>(json).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("ImageLanguage.json");
      ImageLanguages = JsonConvert.DeserializeObject<ImageLanguage[]>(json).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("KeyboardIdentifier.json");
      KeyboardIdentifiers = JsonConvert.DeserializeObject<KeyboardIdentifier[]>(json).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("GeoId.json");
      GeoLocations = JsonConvert.DeserializeObject<GeoLocation[]>(json).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("UserLocale.json");
      JsonConverter[] converters = [new KeyboardConverter(this), new GeoLocationConverter(this)];
      UserLocales = JsonConvert.DeserializeObject<UserLocale[]>(json, converters).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("WindowsEdition.json");
      WindowsEditions = JsonConvert.DeserializeObject<WindowsEdition[]>(json).ToKeyedDictionary();
    }
    {
      string json = Util.StringFromResource("TimeOffset.json");
      TimeOffsets = JsonConvert.DeserializeObject<TimeOffset[]>(json).ToKeyedDictionary();
    }

    {
      VerifyUniqueKeys(Components.Values, e => e.Id);
    }
    {
      VerifyUniqueKeys(WindowsEditions.Values, e => e.Id);
      VerifyUniqueKeys(WindowsEditions.Values, e => e.DisplayName);
      VerifyUniqueKeys(WindowsEditions.Values, e => e.ProductKey);
    }
    {
      VerifyUniqueKeys(UserLocales.Values, e => e.Id);
      VerifyUniqueKeys(UserLocales.Values, e => e.DisplayName);
    }
    {
      VerifyUniqueKeys(KeyboardIdentifiers.Values, e => e.Id);
      VerifyUniqueKeys(KeyboardIdentifiers.Values, e => e.DisplayName);
    }
    {
      VerifyUniqueKeys(ImageLanguages.Values, e => e.Id);
      VerifyUniqueKeys(ImageLanguages.Values, e => e.DisplayName);
    }
    {
      VerifyUniqueKeys(TimeOffsets.Values, e => e.Id);
      VerifyUniqueKeys(TimeOffsets.Values, e => e.DisplayName);
    }
  }

  private static void VerifyUniqueKeys<T>(IEnumerable<T> items, Func<T, object> keySelector)
  {
    items.GroupBy(keySelector).ToList().ForEach(group =>
    {
      if (group.Count() != 1)
      {
        throw new ArgumentException($"'{group.Key}' occurs more than once.");
      }
    });
  }

  public ExplicitTimeZoneSettings CreateExplicitTimeZoneSettings(string id)
  {
    return new ExplicitTimeZoneSettings(
      TimeZone: Lookup<TimeOffset>(id)
    );
  }

  public IImmutableDictionary<string, TimeOffset> TimeOffsets { get; }

  public IImmutableDictionary<string, GeoLocation> GeoLocations { get; }

  public IImmutableDictionary<string, Component> Components { get; }

  public IImmutableDictionary<string, Bloatware> Bloatwares { get; }

  public IImmutableDictionary<string, KeyboardIdentifier> KeyboardIdentifiers { get; }

  public IImmutableDictionary<string, UserLocale> UserLocales { get; }

  public IImmutableDictionary<string, ImageLanguage> ImageLanguages { get; }

  public IImmutableDictionary<string, WindowsEdition> WindowsEditions { get; }

  [return: NotNull]
  private static T Lookup<T>(IImmutableDictionary<string, T> dic, string key) where T : IKeyed
  {
    if (dic.TryGetValue(key, out T? value))
    {
      return value;
    }
    else
    {
      throw new ConfigurationException($"Could not find an element of type '{nameof(T)}' with key '{key}'.");
    }
  }

  [return: NotNull]
  public T Lookup<T>(string key) where T : class, IKeyed
  {
    if (typeof(T) == typeof(WindowsEdition))
    {
      return (T)(object)Lookup(WindowsEditions, key);
    }
    if (typeof(T) == typeof(UserLocale))
    {
      return (T)(object)Lookup(UserLocales, key);
    }
    if (typeof(T) == typeof(ImageLanguage))
    {
      return (T)(object)Lookup(ImageLanguages, key);
    }
    if (typeof(T) == typeof(KeyboardIdentifier))
    {
      return (T)(object)Lookup(KeyboardIdentifiers, key);
    }
    if (typeof(T) == typeof(TimeOffset))
    {
      return (T)(object)Lookup(TimeOffsets, key);
    }
    if (typeof(T) == typeof(Bloatware))
    {
      return (T)(object)Lookup(Bloatwares, key);
    }
    if (typeof(T) == typeof(GeoLocation))
    {
      return (T)(object)Lookup(GeoLocations, key);
    }
    throw new NotSupportedException();
  }

  public XmlDocument GenerateXml(Configuration config)
  {
    var doc = Util.XmlDocumentFromResource("autounattend.xml");
    var ns = new XmlNamespaceManager(doc.NameTable);
    ns.AddNamespace("u", "urn:schemas-microsoft-com:unattend");
    ns.AddNamespace("wcm", "http://schemas.microsoft.com/WMIConfig/2002/State");
    ns.AddNamespace("s", Constants.MyNamespaceUri);

    ModifierContext context = new(
      Configuration: config,
      Document: doc,
      NamespaceManager: ns,
      Generator: this,
      UserOnceScript: new UserOnceScript()
    );

    new List<Modifier> {
      new ComputerNameModifier(context),
      new DiskModifier(context),
      new BypassModifier(context),
      new ProductKeyModifier(context),
      new UsersModifier(context),
      new BloatwareModifier(context),
      new LocalesModifier(context),
      new ExpressSettingsModifier(context),
      new WifiModifier(context),
      new EmptyElementsModifier(context),
      new LockoutModifier(context),
      new PasswordExpirationModifier(context),
      new OptimizationsModifier(context),
      new PersonalizationModifier(context),
      new ComponentsModifier(context),
      new TimeZoneModifier(context),
      new WdacModifier(context),
      new ScriptModifier(context),
      new UserOnceModifier(context),
      new OrderModifier(context),
      new ProcessorArchitectureModifier(context),
      new PrettyModifier(context),
    }.ForEach(modifier =>
    {
      modifier.Process();
    });

    Util.ValidateAgainstSchema(doc, "autounattend.xsd");

    return doc;
  }

  /// <summary>
  /// Serializes an <c>autounattend.xml</c> document such that it can be reliably processed. Windows Setup expects 
  /// the document to have an <c>encoding="utf-8"</c> encoding declaration, but actually only supports ASCII characters.
  /// </summary>
  public static byte[] Serialize(XmlDocument doc)
  {
    using var mstr = new MemoryStream();
    {
      using StreamWriter sw = new(mstr, encoding: Encoding.ASCII, leaveOpen: true);
      sw.Write(@"<?xml version=""1.0"" encoding=""utf-8""?>" + "\r\n");
      sw.Close();
    }
    {
      using var writer = XmlWriter.Create(mstr, new XmlWriterSettings()
      {
        Encoding = Encoding.ASCII,
        OmitXmlDeclaration = true,
        CloseOutput = true,
        Indent = true,
        IndentChars = "\t",
        NewLineChars = "\r\n",
      });
      doc.Save(writer);
      writer.Close();
    }
    return mstr.ToArray();
  }
}

public record class ModifierContext(
  XmlDocument Document,
  XmlNamespaceManager NamespaceManager,
  Configuration Configuration,
  UnattendGenerator Generator,
  UserOnceScript UserOnceScript
);

abstract class Modifier(ModifierContext context)
{
  public XmlDocument Document { get; } = context.Document;

  public XmlNamespaceManager NamespaceManager { get; } = context.NamespaceManager;

  public Configuration Configuration { get; } = context.Configuration;

  public UnattendGenerator Generator { get; } = context.Generator;

  public UserOnceScript UserOnceScript { get; } = context.UserOnceScript;

  public XmlElement NewSimpleElement(string name, XmlElement parent, string innerText)
  {
    return Util.NewSimpleElement(name, parent, innerText, Document, NamespaceManager);
  }

  public XmlElement NewElement(string name, XmlElement parent)
  {
    return Util.NewElement(name, parent, Document, NamespaceManager);
  }

  public abstract void Process();

  public CommandAppender GetAppender(CommandConfig config)
  {
    return new CommandAppender(Document, NamespaceManager, config);
  }

  public void AddXmlFile(XmlDocument xml, string path)
  {
    string ToPrettyString()
    {
      using var sw = new StringWriter();
      using var writer = XmlWriter.Create(sw, new XmlWriterSettings()
      {
        CloseOutput = true,
        OmitXmlDeclaration = true,
        Indent = true,
        IndentChars = "\t",
        NewLineChars = "\r\n",
      });
      xml.Save(writer);
      writer.Close();
      return sw.ToString();
    }

    AddFile(ToPrettyString(), path);
  }

  public void AddTextFile(string content, string path)
  {
    AddFile(content, path);
  }

  private void AddFile(string content, string path)
  {
    {
      XmlNode root = Document.SelectSingleNodeOrThrow("/u:unattend", NamespaceManager);
      XmlNode? extensions = root.SelectSingleNode("s:Extensions", NamespaceManager);
      if (extensions == null)
      {
        extensions = Document.CreateElement("Extensions", Constants.MyNamespaceUri);
        root.AppendChild(extensions);

        XmlNode extractScript = Document.CreateElement("ExtractScript", Constants.MyNamespaceUri);
        extensions.AppendChild(extractScript);
        extractScript.AppendChild(
          Document.CreateTextNode(
            Util.Indent(
              Util.StringFromResource("ExtractScripts.ps1")
            )
          )
        );

        CommandAppender appender = GetAppender(CommandConfig.Specialize);
        appender.Append(
          CommandBuilder.PowerShellCommand(@"$xml = [xml]::new(); $xml.Load('C:\Windows\Panther\unattend.xml'); $sb = [scriptblock]::Create( $xml.unattend.Extensions.ExtractScript ); Invoke-Command -ScriptBlock $sb -ArgumentList $xml;")
        );
      }

      XmlElement file = Document.CreateElement("File", Constants.MyNamespaceUri);
      file.SetAttribute("path", path);
      extensions.AppendChild(file);
      file.AppendChild(Document.CreateTextNode(Util.Indent(content)));
    }
  }
}