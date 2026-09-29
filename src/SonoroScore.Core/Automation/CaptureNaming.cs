// © 2026 RadianceNoMore (Sonoro-Score, MIT).
// The ONE place the capture filenames are defined: echo_pXX_rYY_cZZ_idxNNN.png.
// The scanner, the review studio and every verified-truth file depend on this exact
// pattern, so it must never drift between capture paths again.

namespace AlephalSonata.Automation;

public static class CaptureNaming
{
    /// <summary>Builds one capture filename. page/row/col are 0-based, index is the
    /// running 1-based capture counter (zero-padded to 3 digits, grows beyond 999).</summary>
    public static string FileName(int page, int row, int col, int index)
        => $"echo_p{page + 1:D2}_r{row + 1:D2}_c{col + 1:D2}_idx{index:D3}.png";
}
