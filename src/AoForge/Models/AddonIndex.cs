using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AoForge.Models;

// index.json: the list of addons AoForge knows and how to install each one.
[DataContract]
public sealed class AddonIndex
{
    [DataMember(Name = "schema")] public int Schema { get; set; }
    [DataMember(Name = "addons")] public List<AddonDef> Addons { get; set; } = new();
}

[DataContract]
public sealed class AddonDef
{
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; }
    [DataMember(Name = "author")] public string Author { get; set; }
    [DataMember(Name = "summary")] public string Summary { get; set; }
    [DataMember(Name = "description")] public string Description { get; set; }
    [DataMember(Name = "repo")] public string Repo { get; set; }           // owner/name on GitHub
    [DataMember(Name = "homepage")] public string Homepage { get; set; }
    [DataMember(Name = "license")] public string License { get; set; }
    [DataMember(Name = "assetPattern")] public string AssetPattern { get; set; } // regex on the release asset name
    [DataMember(Name = "notes")] public string Notes { get; set; }          // shown after installing
    [DataMember(Name = "options")] public List<AddonOption> Options { get; set; }
    [DataMember(Name = "steps")] public List<RecipeStep> Steps { get; set; }
    [DataMember(Name = "detect")] public List<DetectRule> Detect { get; set; }
    [DataMember(Name = "userFiles")] public List<string> UserFiles { get; set; } // files the addon creates; wildcards allowed

    public List<AddonOption> OptionList => Options ??= new();
    public List<RecipeStep> StepList => Steps ??= new();
    public List<DetectRule> DetectList => Detect ??= new();
    public List<string> UserFileList => UserFiles ??= new();
}

[DataContract]
public sealed class AddonOption
{
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "label")] public string Label { get; set; }
    [DataMember(Name = "description")] public string Description { get; set; }
    [DataMember(Name = "default")] public bool Default { get; set; }
}

public static class StepAction
{
    public const string Copy = "copy";                         // from (zip) -> to (client folder)
    public const string PreserveOriginal = "preserveOriginal"; // rename the game's file to "as" once
}

[DataContract]
public sealed class RecipeStep
{
    [DataMember(Name = "action")] public string Action { get; set; }
    [DataMember(Name = "from")] public string From { get; set; }
    [DataMember(Name = "to")] public string To { get; set; }
    [DataMember(Name = "file")] public string File { get; set; }
    [DataMember(Name = "as")] public string As { get; set; }
    [DataMember(Name = "option")] public string Option { get; set; } // only when this option is on
}

// A copy of the addon installed by hand is recognised by a file, optionally with this ProductName.
[DataContract]
public sealed class DetectRule
{
    [DataMember(Name = "file")] public string File { get; set; }
    [DataMember(Name = "productName")] public string ProductName { get; set; }
}
