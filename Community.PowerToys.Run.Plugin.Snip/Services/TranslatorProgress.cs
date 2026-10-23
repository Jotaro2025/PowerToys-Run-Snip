// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Progress tick fired as translation lines finish (best effort, engine-dependent).</summary>
/// <param name="Completed">Lines whose translation is final so far.</param>
/// <param name="Total">Total non-empty lines queued for translation.</param>
public readonly record struct TranslatorProgress(int Completed, int Total);
