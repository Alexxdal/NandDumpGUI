using Microsoft.Win32;
using NandDumpGUI.Core;
using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NandDumpGUI
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();
            PolyCombo.ItemsSource = PrimitivePolynomials.All;
            PolyCombo.DisplayMemberPath = "Display";

            // default: 0x5803 (come ora)
            PolyCombo.Text = "0x5803";

            // aggiorna label m/notes quando cambia testo
            PolyCombo.LostKeyboardFocus += (_, __) => UpdatePolyInfo();
            PolyCombo.SelectionChanged += (_, __) => UpdatePolyInfo();
            UpdatePolyInfo();
        }

        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "RAW dump|*.bin;*.img;*.*" };
            if (dlg.ShowDialog() == true) InpPath.Text = dlg.FileName;
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Data-only|*.bin;*.img;*.*" };
            if (dlg.ShowDialog() == true) OutPath.Text = dlg.FileName;
        }

        private void BrowseOutRaw_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Fixed RAW|*.bin;*.img;*.*" };
            if (dlg.ShowDialog() == true) OutRawPath.Text = dlg.FileName;
        }

        private static uint ParsePoly(string text)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(text.Substring(2), 16);

            // heuristic: if contains A-F -> hex
            foreach (char c in text)
                if ((c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))
                    return Convert.ToUInt32(text, 16);

            return Convert.ToUInt32(text, 10);
        }

        private static TransformKind ParseTransform(string s) => s switch
        {
            "inv" => TransformKind.Inv,
            "bitrev" => TransformKind.Bitrev,
            "inv+bitrev" => TransformKind.InvBitrev,
            _ => TransformKind.None
        };

        private static int DegreeFromPoly(uint poly)
        {
            if (poly == 0) throw new ArgumentException("poly=0 is invalid");
            return 31 - BitOperations.LeadingZeroCount(poly);
        }

        private NandLayout ReadLayoutFromUi()
        {
            int page = int.Parse(PageSizeBox.Text.Trim());
            int oob = int.Parse(OobSizeBox.Text.Trim());
            int sec = int.Parse(SectorSizeBox.Text.Trim());
            int chunk = int.Parse(OobChunkBox.Text.Trim());
            int ofs = int.Parse(EccOfsBox.Text.Trim());
            int len = int.Parse(EccLenBox.Text.Trim());

            var layout = new NandLayout(page, oob, sec, chunk, ofs, len);
            layout.Validate();
            return layout;
        }

        private void AppendLog(string s)
        {
            LogBox.AppendText(s + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            string inp = InpPath.Text.Trim();
            string outData = OutPath.Text.Trim();
            bool saveRaw = SaveRawCheck.IsChecked == true;
            string? outRaw = saveRaw ? OutRawPath.Text.Trim() : null;

            if (string.IsNullOrWhiteSpace(inp) || !File.Exists(inp))
            {
                MessageBox.Show("Please select a valid input RAW file.");
                return;
            }
            if (string.IsNullOrWhiteSpace(outData))
            {
                MessageBox.Show("Please select a valid output data file.");
                return;
            }
            if (saveRaw && string.IsNullOrWhiteSpace(outRaw))
            {
                MessageBox.Show("You enabled 'Save fixed RAW' but did not select the fixed RAW output path.");
                return;
            }

            uint poly;
            int t;
            int oobDataBytes;
            try
            {
                poly = PolyCombo.SelectedItem is PrimitivePolyPreset preset
                ? preset.Poly
                : ParsePoly(PolyCombo.Text);
                t = int.Parse(TBox.Text.Trim());
                oobDataBytes = int.Parse(OobDataBox.Text.Trim());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Invalid parameters: " + ex.Message);
                return;
            }

            NandLayout layout;
            try
            {
                layout = ReadLayoutFromUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Invalid NAND layout: " + ex.Message);
                return;
            }

            if (oobDataBytes < 0 || oobDataBytes > layout.EccOfs)
            {
                MessageBox.Show($"OOB data bytes must be between 0 and ECC offset ({layout.EccOfs}).");
                return;
            }

            var tf = ParseTransform(((TransformBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()) ?? "none");
            bool skipErased = SkipErasedCheck.IsChecked == true;
            bool rewriteEcc = RewriteEccCheck.IsChecked == true;

            var opt = new FixOptions(
                OobDataBytes: oobDataBytes,
                Transform: tf,
                SkipErased: skipErased,
                RewriteEccInRaw: rewriteEcc
            );

            // Create BCH context
            int m = DegreeFromPoly(poly);

            // IMPORTANT:
            // If your BchContext factory has a different name, adjust this line only.
            using var bch = BchContext.Create(m, t, poly, swapBits: false);

            var fixer = new NandFixer();

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            Prog.Value = 0;
            LogBox.Clear();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var progress = new Progress<double>(p => Prog.Value = p);
            var log = new Progress<string>(s => AppendLog(s));

            FixReport? report = null;
            try
            {
                report = await fixer.ProcessAsync(
                    bch, layout, opt,
                    inputRaw: inp,
                    outputDataOnly: outData,
                    outputFixedRaw: saveRaw ? outRaw : null,
                    progress: progress,
                    log: log,
                    ct: ct);

                AppendLog("[+] Done.");
                ShowFixQualityWarning(report);
            }
            catch (OperationCanceledException)
            {
                AppendLog("[-] Operation cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
            finally
            {
                _cts = null;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }

        private void ShowFixQualityWarning(FixReport? r)
        {
            // Heuristics: non è “matematica”, ma utile in pratica.
            // - Se moltissimi settori sono uncorrectable => parametri quasi certamente sbagliati (poly/transform/oob layout).
            // - Se 0 bitflips e 0 modifiche: può essere già “pulito”, MA se uncorrectable > 0 allora è sospetto.
            if (r?.CheckedSectors <= 0)
            {
                MessageBox.Show(
                    "No non-erased sectors were processed.\n\n" +
                    "This may indicate an erased/blank dump or a wrong NAND layout (page/oob/step/chunk offsets).",
                    "Result quality",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (r?.UncorrectableSectors == 0)
                return; // tutto ok: niente warning

            string severityTitle = "Fix may have failed";
            MessageBoxImage icon = MessageBoxImage.Warning;

            double? ratio = r?.UncorrectableRatio;

            string hint =
                "- Verify NAND layout (Page/OOB sizes, ECC step size, OOB chunk size, ECC offset/length)\n" +
                "- Verify transform (none / inv / bitrev / inv+bitrev)\n" +
                "- Try a different primitive polynomial preset (e.g., 0x5803 vs 0x402B)\n" +
                "- If the dump is physically noisy, some sectors may be genuinely uncorrectable";

            string msg =
                $"Uncorrectable sectors: {r?.UncorrectableSectors} / {r?.CheckedSectors} ({ratio:P1})\n" +
                $"Total corrected bitflips: {r?.TotalBitflips}\n" +
                $"Modified pages: {r?.PagesTouched}\n\n";

            if (ratio >= 0.20)
            {
                msg +=
                    "This is a high uncorrectable ratio.\n" +
                    "Most likely the parameters are wrong (polynomial/transform/layout), or the dump quality is very poor.\n\n";
            }
            else if (ratio >= 0.02)
            {
                msg +=
                    "Some sectors could not be corrected.\n" +
                    "This might still be usable, but it can also indicate a parameter mismatch.\n\n";
            }
            else
            {
                msg +=
                    "Only a small number of sectors were uncorrectable.\n" +
                    "This is often acceptable, but keep an eye on filesystem extraction errors.\n\n";
                icon = MessageBoxImage.Information;
                severityTitle = "Fix completed with minor issues";
            }

            msg += "Suggestions:\n" + hint;

            MessageBox.Show(msg, severityTitle, MessageBoxButton.OK, icon);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void UserGuide_Click(object sender, RoutedEventArgs e)
        {
            var w = new HelpWindow();
            w.Owner = this;
            w.Show();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var w = new AboutWindow();
            w.Owner = this;
            w.ShowDialog();
        }

        private void UpdatePolyInfo()
        {
            try
            {
                uint poly = PolyCombo.SelectedItem is PrimitivePolyPreset preset
                    ? preset.Poly
                    : ParsePoly(PolyCombo.Text);

                int m = DegreeFromPoly(poly);
                var found = PrimitivePolynomials.FindByPoly(poly);

                /*if (PolyInfoText != null)
                {
                    PolyInfoText.Text = found != null
                        ? $"m={m} • {found.Notes}"
                        : $"m={m} • custom polynomial";
                }*/
            }
            catch
            {
                /*if (PolyInfoText != null)
                    PolyInfoText.Text = "invalid polynomial";*/
            }
        }

        private uint GetSelectedPoly()
        {
            if (PolyCombo.SelectedItem is PrimitivePolyPreset p)
                return p.Poly;

            return ParsePoly(PolyCombo.Text);
        }

        private async void QuickTest_Click(object sender, RoutedEventArgs e)
        {
            string inp = InpPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(inp) || !File.Exists(inp))
            {
                MessageBox.Show("Please select a valid input RAW file first.");
                return;
            }

            // polinomi (presets + manuale)
            uint manualPoly = GetSelectedPoly();
            var polys = PrimitivePolynomials.All.Select(p => p.Poly).ToList();
            if (!polys.Contains(manualPoly))
                polys.Add(manualPoly);

            // t candidates (include quello UI + alcuni classici)
            var tCandidates = new List<int> { int.Parse(TBox.Text.Trim()), 2, 4, 8, 12, 16, 24, 32, 64 }
                .Distinct()
                .Where(x => x > 0)
                .ToList();

            var transforms = new[] { TransformKind.None, TransformKind.Inv, TransformKind.Bitrev, TransformKind.InvBitrev };

            // UI state
            StartButton.IsEnabled = false;
            QuickTestButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            Prog.Value = 0;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var progress = new Progress<double>(p => Prog.Value = p);
            var log = new Progress<string>(s => AppendLog(s));

            try
            {
                AppendLog("[QT] Starting auto layout + parameter quick test...");

                var res = await AutoLayoutTester.RunAsync(
                    inputRaw: inp,
                    candidatePolys: polys,
                    tCandidates: tCandidates,
                    transforms: transforms,
                    pagesToScoreLayouts: 64,
                    pagesToSampleForParams: 128,
                    maxLayoutsToTest: 6,
                    seed: 123,
                    log: log,
                    progress: progress,
                    ct: ct);

                AppendLog("");
                AppendLog("[QT] Top results:");
                foreach (var top in res.Top)
                {
                    AppendLog($"   layout={top.Layout.PageSize}+{top.Layout.OobSize} (sector={top.Layout.SectorSize}, chunk={top.Layout.OobChunk}, eccOfs={top.Layout.EccOfs}, eccLen={top.Layout.EccLen})");
                    AppendLog($"   offset={top.Offset} score={top.LayoutScore:F3}  best={top.BestParams}  stats={top.Stats}");
                    AppendLog("");
                }

                // Applica BEST alla UI
                ApplyLayoutToUi(res.Best.Layout);
                PolyCombo.Text = $"0x{res.Best.BestParams.Poly:X}";
                TBox.Text = res.Best.BestParams.T.ToString();
                OobDataBox.Text = res.Best.BestParams.OobDataBytes.ToString();
                SetTransformSelection(res.Best.BestParams.Transform);
                UpdatePolyInfo();

                // NB: se Offset != 0, il file sembra avere un header/trailer.
                // Per ora te lo segnalo: puoi aggiungere più avanti una "Input Offset" option al fixer.
                string offsetNote = res.Best.Offset == 0
                    ? "Offset looks aligned (0)."
                    : $"Detected a likely alignment offset of {res.Best.Offset} bytes (header/trailer?). Consider trimming or adding an input-offset option.";

                MessageBox.Show(
                    $"Suggested settings:\n\n" +
                    $"Layout: page={res.Best.Layout.PageSize}, oob={res.Best.Layout.OobSize}, sector={res.Best.Layout.SectorSize}\n" +
                    $"Chunk={res.Best.Layout.OobChunk}, ECC ofs={res.Best.Layout.EccOfs}, ECC len={res.Best.Layout.EccLen}\n\n" +
                    $"Params: {res.Best.BestParams}\n" +
                    $"Stats: {res.Best.Stats}\n\n" +
                    offsetNote,
                    "Quick Test Result",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                AppendLog("[QT] Cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
            finally
            {
                _cts = null;
                StartButton.IsEnabled = true;
                QuickTestButton.IsEnabled = true;
                Prog.Value = 100;
            }
        }

        private void ApplyLayoutToUi(NandLayout l)
        {
            PageSizeBox.Text = l.PageSize.ToString();
            OobSizeBox.Text = l.OobSize.ToString();
            SectorSizeBox.Text = l.SectorSize.ToString();
            OobChunkBox.Text = l.OobChunk.ToString();
            EccOfsBox.Text = l.EccOfs.ToString();
            EccLenBox.Text = l.EccLen.ToString();
        }

        private void SetTransformSelection(TransformKind k)
        {
            string text = k switch
            {
                TransformKind.Inv => "inv",
                TransformKind.Bitrev => "bitrev",
                TransformKind.InvBitrev => "inv+bitrev",
                _ => "none"
            };

            for (int i = 0; i < TransformBox.Items.Count; i++)
            {
                if (TransformBox.Items[i] is ComboBoxItem cbi &&
                    string.Equals(cbi.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    TransformBox.SelectedIndex = i;
                    return;
                }
            }

            TransformBox.SelectedIndex = 0;
        }
    }
}
