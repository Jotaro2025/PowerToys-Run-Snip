// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Sdcb.HyMT2Sharp.Backends;
using Sdcb.HyMT2Sharp.Model;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// In-process HyMT2Sharp translator. The GGUF is loaded lazily on first use and
/// guarded by a single-flight gate (the model is not thread-safe). Weights are
/// never bundled: the user points <see cref="SnipSettings.HyMtGgufPath"/> at a
/// local GGUF file.
/// </summary>
public sealed class HyMT2SharpTranslator : ITranslator, IDisposable
{
    private readonly string _ggufPath;
    private readonly HyMtBackendKind _backendKind;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly object _loadLock = new object();
    private HunyuanDenseModel _model;
    private bool _disposed;

    public HyMT2SharpTranslator(string ggufPath, HyMtBackendKind backendKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ggufPath);
        _ggufPath = ggufPath;
        _backendKind = backendKind;
    }

    /// <summary>True when another TranslateAsync call currently holds the single-flight gate.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new TranslatorException(Properties.Resources.tr_err_no_language);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        // Prefer a zero-timeout try so callers can show a busy hint before we queue.
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            EnsureModelLoaded();
            var results = new string[texts.Count];
            for (int i = 0; i < texts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = texts[i] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(source))
                {
                    results[i] = string.Empty;
                    continue;
                }

                results[i] = await GenerateAsync(
                    TranslatorPrompts.Build(targetLanguage, source),
                    maxTokens: Math.Clamp(source.Length * 4, 64, 512),
                    cancellationToken).ConfigureAwait(false);
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureModelLoaded()
    {
        if (_model != null)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_model != null)
            {
                return;
            }

            if (!File.Exists(_ggufPath))
            {
                throw new TranslatorException($"{Properties.Resources.tr_err_gguf_missing} {_ggufPath}");
            }

            try
            {
                IComputeBackend backend = CreateBackend(_backendKind);
                _model = new HunyuanDenseModel(_ggufPath, backend: backend);
            }
            catch (TranslatorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip HyMT2Sharp load failed: {ex.Message}", ex, typeof(HyMT2SharpTranslator));
                throw new TranslatorException($"{Properties.Resources.tr_err_hymt_load} {ex.Message}", ex);
            }
        }
    }

    private static IComputeBackend CreateBackend(HyMtBackendKind kind)
    {
        // BackendFactory.Create() / Create("cpu"|"vulkan") — null means pure CPU.
        return kind switch
        {
            HyMtBackendKind.Cpu => BackendFactory.Create("cpu"),
            HyMtBackendKind.Vulkan => BackendFactory.Create("vulkan"),
            _ => BackendFactory.Create(),
        };
    }

    private async Task<string> GenerateAsync(string user, int maxTokens, CancellationToken cancellationToken)
    {
        try
        {
            var pieces = new List<string>();
            await foreach (var piece in GenerateStreamAsync(_model, user, maxTokens, cancellationToken).ConfigureAwait(false))
            {
                pieces.Add(piece);
            }

            return string.Concat(pieces).Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip HyMT2Sharp generate failed: {ex.Message}", ex, typeof(HyMT2SharpTranslator));
            throw new TranslatorException($"{Properties.Resources.tr_err_hymt_failed} {ex.Message}", ex);
        }
    }

    private static async IAsyncEnumerable<string> GenerateStreamAsync(
        HunyuanDenseModel model,
        string user,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int[] prompt = model.Tokenizer.Encode(ChatTemplate.RenderHunyuanDense([new ChatMessage("user", user)]));
        float[] logits = model.Forward(model.AlignPrompt(prompt).Suffix);
        await Task.Yield();

        var generated = new List<int>();
        string visible = string.Empty;
        for (int i = 0; i < maxTokens; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int token = Sampler.ArgMax(logits);
            if (model.Tokenizer.IsStop(token))
            {
                yield break;
            }

            generated.Add(token);

            // A trailing U+FFFD is a UTF-8 sequence split across tokens; wait for the next token.
            string next = model.Tokenizer.DecodeVisible(generated).TrimEnd('\uFFFD');
            if (next.Length > visible.Length && next.StartsWith(visible, StringComparison.Ordinal))
            {
                yield return next[visible.Length..];
                visible = next;
            }

            logits = model.Forward([token]);
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            _model?.Dispose();
            _model = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
