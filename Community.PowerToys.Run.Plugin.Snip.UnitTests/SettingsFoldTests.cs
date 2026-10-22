// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>
/// Documents the Settings UI limitation: PowerToys Run AdditionalOptions cannot
/// dynamically hide engine-specific fields, so we rely on label prefixes.
/// </summary>
[TestClass]
public class SettingsFoldTests
{
    [TestMethod]
    public void HttpOptionLabels_UseHttpPrefix()
    {
        Assert.IsTrue(Resources.opt_tr_http_url_label.StartsWith("HTTP ·", System.StringComparison.Ordinal));
        Assert.IsTrue(Resources.opt_tr_http_key_label.StartsWith("HTTP ·", System.StringComparison.Ordinal));
        Assert.IsTrue(Resources.opt_tr_http_model_label.StartsWith("HTTP ·", System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void HyMtOptionLabels_UseHyMtPrefix()
    {
        Assert.IsTrue(Resources.opt_tr_hymt_path_label.StartsWith("HyMT ·", System.StringComparison.Ordinal));
        Assert.IsTrue(Resources.opt_tr_hymt_backend_label.StartsWith("HyMT ·", System.StringComparison.Ordinal));
        Assert.IsTrue(Resources.opt_tr_hymt_download_label.StartsWith("HyMT ·", System.StringComparison.Ordinal));
        Assert.IsTrue(Resources.opt_tr_hymt_custom_url_label.StartsWith("HyMT ·", System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void TranslatorProviderDescription_MentionsNoDynamicHide()
    {
        StringAssert.Contains(Resources.opt_tr_provider_desc, "cannot hide");
    }
}
