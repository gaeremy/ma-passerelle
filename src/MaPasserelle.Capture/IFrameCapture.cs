// SPDX-License-Identifier: GPL-3.0-or-later
using Vortice.Direct3D11;

namespace MaPasserelle.Capture;

internal enum CaptureResult
{
    /// <summary>Rien de nouveau : l'image précédente reste valable.</summary>
    NoFrame,
    /// <summary>Une nouvelle image a été copiée dans la texture de destination.</summary>
    Frame,
    /// <summary>La source a changé de taille : recréer la texture de destination puis réessayer.</summary>
    SizeChanged,
    /// <summary>La source n'existe plus (fenêtre fermée, écran débranché).</summary>
    Ended,
}

/// <summary>Une source d'images BGRA sur le GPU : un écran ou une fenêtre.</summary>
internal interface IFrameCapture : IDisposable
{
    ID3D11Device Device { get; }
    int Width { get; }
    int Height { get; }
    string Name { get; }
    CaptureResult TryCaptureInto(ID3D11Texture2D dest, int timeoutMs);
}
