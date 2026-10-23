// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Trimmed from the PowerToys host tree: suppressions targeting other modules
// (MouseWithoutBorders, Peek, RegistryPreview, AdvancedPaste, Hosts, …)
// do not apply to this standalone plugin and were removed.
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1009:ClosingParenthesisMustBeSpacedCorrectly", Justification = "All current violations are due to Tuple shorthand and so valid.")]

[assembly: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101:PrefixLocalCallsWithThis", Justification = "We follow the C# Core Coding Style which avoids using `this` unless absolutely necessary.")]

[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1201:ElementsMustAppearInTheCorrectOrder", Justification = "It is not a priority and has high impact in code changes.")]
[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1202:ElementsMustBeOrderedByAccess", Justification = "It is not a priority and has high impact in code changes.")]
[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1203:ConstantsMustAppearBeforeFields", Justification = "It is not a priority and has high impact in code changes.")]
[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:StaticElementsMustAppearBeforeInstanceElements", Justification = "It is not a priority and has high impact in code changes.")]

[assembly: SuppressMessage("StyleCop.CSharp.NamingRules", "SA1309:FieldNamesMustNotBeginWithUnderscore", Justification = "We follow the C# Core Coding Style which uses underscores as prefixes rather than using `this.`.")]

[assembly: SuppressMessage("StyleCop.CSharp.SpecialRules", "SA0001:XmlCommentAnalysisDisabled", Justification = "Not enabled as we don't want or need XML documentation.")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1629:DocumentationTextMustEndWithAPeriod", Justification = "Not enabled as we don't want or need XML documentation.")]

// Non general suppressions
[assembly: SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Used in a lot of places for meaningful method names")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Static methods may improve performance but decrease maintainability")]
[assembly: SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Renaming everything would be a lot of work. It does not do any harm if an EventHandler delegate ends with the suffix EventHandler. Besides this, the Rule causes some false positives.")]
[assembly: SuppressMessage("Performance", "CA1838:Avoid 'StringBuilder' parameters for P/Invokes", Justification = "We are not concerned about the performance impact of marshaling a StringBuilder")]

// Uninstantiated TestFixture classes
[assembly: SuppressMessage("Microsoft.Performance", "CA1812: Avoid uninstantiated internal classes", Scope = "module", Justification = "CA1812 will be thrown for every file in the test project. This is mentioned here: dotnet/roslyn-analyzers#1830")]

// Code quality
[assembly: SuppressMessage("CodeQuality", "IDE0076:Invalid global 'SuppressMessageAttribute'", Justification = "Affect predefined suppressions.")]

[assembly: SuppressMessage(
    "Microsoft.Usage",
    "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "ScrollCaptureController lifetime is tied to the snip session; CleanupResources / Cancel dispose it.",
    Scope = "type",
    Target = "~T:Community.PowerToys.Run.Plugin.Snip.UI.SnipWindow")]

[assembly: SuppressMessage(
    "Microsoft.Usage",
    "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "CancellationTokenSource is disposed in Stop/Cancel paths.",
    Scope = "type",
    Target = "~T:Community.PowerToys.Run.Plugin.Snip.Services.ScrollCaptureController")]
