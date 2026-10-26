using System.Text.Encodings.Web;
using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tesseract;
namespace PrescriptionScanner
{
    public class PrescriptionModel
    {
        [JsonPropertyName("person")]
        public string? Person { get; set; }

        [JsonPropertyName("tray")]
        public int Tray { get; set; }

        [JsonPropertyName("dose")]
        public string? Dose { get; set; }

        [JsonPropertyName("frequency")]
        public string? Frequency { get; set; }

        [JsonPropertyName("timing")]
        public string? Timing { get; set; }

        [JsonPropertyName("startDate")]
        public string? StartDate { get; set; }

        [JsonPropertyName("days")]
        public int? Days { get; set; }

        [JsonPropertyName("medications")]
        public List<MedicationModel> Medications { get; set; } = [];
    }

    public class MedicationModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("dose")]
        public string? Dose { get; set; }

        [JsonPropertyName("timing")]
        public string? Timing { get; set; }

        [JsonPropertyName("frequency")]
        public string? Frequency { get; set; }

        [JsonPropertyName("days")]
        public int? Days { get; set; }

        [JsonPropertyName("totalQuantity")]
        public decimal? TotalQuantity { get; set; }
    }

    public class PatientEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public record PatientCandidate(string Name, int Similarity);

    internal sealed class MultiplePrescriptionsException : Exception
    {
        public MultiplePrescriptionsException() : base("偵測到多個姓名欄位，請將上下藥單分開裁切，每次上傳一位病患的藥單。") { }
    }

    class Program
    {
        private static readonly object PaddleOcrLock = new();
        private static Process? paddleOcrWorker;

        static Program()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopPaddleOcrWorker();
        }

        static void Main(string[] args)
        {
            if (args.Contains("--serve"))
            {
                OcrWebServer.Run(args.Where(arg => arg != "--serve").ToArray());
                return;
            }
            // 執行檔的工作目錄不一定是專案目錄，因此預設從輸出目錄讀取圖片。
            // 也可將圖片完整路徑當作第一個命令列參數傳入。
            // 多張圖片在同一個程序處理，沿用 Python worker，避免逐張重新載入模型。
            string[] imagePaths = args.Length > 0
                ? args.Select(Path.GetFullPath).ToArray()
                : [Path.Combine(AppContext.BaseDirectory, "S__36208690.jpg")];
            int assignedTray = 2; // 設定對應的藥盤編號

            foreach (string imagePath in imagePaths)
            {
                var elapsed = Stopwatch.StartNew();
                try
                {
                    Console.WriteLine($"讀取圖片：{imagePath}");
                    string jsonOutput = ParsePrescription(imagePath, assignedTray);
                    Console.WriteLine("=== 辨識成功，產生的交接 JSON ===");
                    Console.WriteLine(jsonOutput);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"辨識失敗：{ex.Message}");
                    Environment.ExitCode = 1;
                }
                Console.WriteLine($"處理耗時：{elapsed.Elapsed.TotalSeconds:F2} 秒");
            }

            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("\n按任意鍵結束...");
                Console.ReadKey();
            }
        }

        public static string ParsePrescription(string imgPath, int tray, string profile = "original")
        {
            if (string.IsNullOrWhiteSpace(imgPath) || !File.Exists(imgPath))
            {
                throw new FileNotFoundException(
                    $"找不到藥單圖片：{Path.GetFullPath(imgPath)}。請將圖片放在執行檔旁，或以第一個命令列參數提供圖片完整路徑。",
                    imgPath);
            }

            if (profile is not ("original" or "fast")) throw new ArgumentException("不支援的辨識模式。", nameof(profile));
            var recognizedTexts = RecognizeTextWithPaddleOcr(imgPath, profile);
            // The current response represents exactly one patient. Do not merge
            // medication rows from multiple prescription sections into that record.
            if (recognizedTexts.Sum(text => Regex.Matches(text, @"姓\s*名\s*[:：]").Count) > 1)
                throw new MultiplePrescriptionsException();
            string recognizedText = string.Join(Environment.NewLine, recognizedTexts);

            var record = new PrescriptionModel { Tray = tray };

            // 直接採用 OCR 在「姓名：」欄擷取到的結果；未辨識到時才維持 null。
            record.Person = FindOcrPatientName(recognizedTexts);

                // 日期必須在任一輪 OCR 中出現完整且合理的民國年月日，才會輸出。
            record.StartDate = FindMostLikelyPrescriptionDate(recognizedTexts);

                // 3. 擷取實際領藥天數（例如：領藥 3 日份、早餐後吃 28）
            var daysMatch = Regex.Match(recognizedText,
                @"(?:領藥\s*)?(\d+)\s*日\s*份|(?:早餐後吃|午餐後吃|晚餐後吃|睡前)\s*(\d{1,3})");
            record.Days = daysMatch.Success
                ? int.Parse(daysMatch.Groups[1].Success ? daysMatch.Groups[1].Value : daysMatch.Groups[2].Value)
                : null;

                // 4. 擷取實際劑量與頻率（例如：1.00 錠 x3x3）。
                // OCR 有時會把「錠」誤讀為其他字，故以可靠的數字與 x 次數格式為準；
                // 單位未能確認時不虛構單位。
            var usageMatches = Regex.Matches(recognizedText,
                @"(?<amount>\d+(?:[.．﹒，,]\d+)?)\s*(?:[錠包粒片顆]|[^\d\s])?\s*[xX×*]\s*(?<times>\d+)\s*[xX×*]\s*(?<days>\d+)");
            if (usageMatches.Count > 0)
            {
                    // 一張處方常含多列藥品。取同一張藥單中最常出現的 OCR 劑量與頻率，
                    // 可避免單列模糊造成的誤讀，且不引入任何寫死的預設數值。
                var parsedUsages = usageMatches
                        .Select(match => new
                        {
                            Amount = Regex.Replace(match.Groups["amount"].Value, "[．﹒，,]", "."),
                            Times = int.Parse(match.Groups["times"].Value)
                        })
                        .ToList();

                record.Dose = parsedUsages
                        .GroupBy(usage => usage.Amount)
                        .OrderByDescending(group => group.Count())
                        .ThenByDescending(group => group.Key.Length)
                        .First()
                        .Key;

                int times = parsedUsages
                        .GroupBy(usage => usage.Times)
                        .OrderByDescending(group => group.Count())
                        .First()
                        .Key;
                record.Frequency = $"每日{times}次";
            }

            // 此類收據常以「早餐後吃」直接表示服用時間；只取影像中實際辨識到的字。
            var timingMatch = Regex.Match(recognizedText, @"早餐後吃|午餐後吃|晚餐後吃|睡前|飯前|飯後");
            record.Timing = timingMatch.Success ? timingMatch.Value : null;

            // 沒有 x3x3 格式時，仍可從藥物列中擷取實際的單次劑量，例如「1 錠」。
            if (record.Dose is null)
            {
                var doseMatches = Regex.Matches(recognizedText,
                        @"(?<!\d)(?<amount>\d+(?:[．﹒，,]\d+)?)\s*[.．﹒，,]?\s*(?<unit>[錠包粒片顆])")
                    .Select(match =>
                        $"{Regex.Replace(match.Groups["amount"].Value, "[．﹒，,]", ".")} {match.Groups["unit"].Value}")
                    .ToList();
                record.Dose = doseMatches
                    .GroupBy(dose => dose)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault();
            }

            // 藥品欄位是動態陣列：一張藥單有幾項，就輸出幾項；沒有辨識到則輸出空陣列。
            record.Medications = FindMedications(recognizedTexts);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            };

            return JsonSerializer.Serialize(record, options);
        }

        internal static void WarmUpOcr()
        {
            string scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "paddle_ocr_to_json.py");
            string pythonPath = Environment.GetEnvironmentVariable("PADDLE_OCR_PYTHON")
                ?? @"C:\ocr-tools\.venv313\Scripts\python.exe";
            if (!File.Exists(scriptPath) || !File.Exists(pythonPath))
                throw new FileNotFoundException("找不到 PaddleOCR 的 Python 或腳本。");
            lock (PaddleOcrLock) EnsurePaddleOcrWorker(pythonPath, scriptPath, warmup: true);
        }

        private static IReadOnlyList<string> RecognizeTextWithPaddleOcr(string imagePath, string profile)
        {
            string scriptPath = Path.Combine(AppContext.BaseDirectory, "tools", "paddle_ocr_to_json.py");
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("找不到 PaddleOCR 轉換腳本。", scriptPath);
            }

            string pythonPath = Environment.GetEnvironmentVariable("PADDLE_OCR_PYTHON")
                ?? @"C:\ocr-tools\.venv313\Scripts\python.exe";
            if (!File.Exists(pythonPath))
            {
                throw new FileNotFoundException(
                    "找不到 PaddleOCR 的 Python。請設定 PADDLE_OCR_PYTHON 環境變數。",
                    pythonPath);
            }

            lock (PaddleOcrLock)
            {
                EnsurePaddleOcrWorker(pythonPath, scriptPath);
                paddleOcrWorker!.StandardInput.WriteLine(JsonSerializer.Serialize(new { image = imagePath, profile }));
                paddleOcrWorker.StandardInput.Flush();

                using var response = ReadPaddleOcrResponse(paddleOcrWorker, "result");
                if (response.RootElement.TryGetProperty("message", out var message))
                {
                    throw new InvalidOperationException($"PaddleOCR 執行失敗：{message.GetString()}");
                }

                return response.RootElement.GetProperty("data").GetProperty("rec_texts")
                    .EnumerateArray()
                    .Select(element => element.GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!)
                    .ToList();
            }
        }

        private static void EnsurePaddleOcrWorker(string pythonPath, string scriptPath, bool warmup = false)
        {
            if (paddleOcrWorker is not null && !paddleOcrWorker.HasExited) return;

            paddleOcrWorker?.Dispose();
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--worker");
            if (warmup) startInfo.ArgumentList.Add("--warmup");

            paddleOcrWorker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("無法啟動 PaddleOCR。");
            _ = paddleOcrWorker.StandardError.ReadToEndAsync();
            using var ready = ReadPaddleOcrResponse(paddleOcrWorker, "ready");
            if (ready.RootElement.GetProperty("type").GetString() == "error")
            {
                StopPaddleOcrWorker();
                throw new InvalidOperationException("PaddleOCR 模型初始化失敗。");
            }
        }

        private static JsonDocument ReadPaddleOcrResponse(Process process, string expectedType)
        {
            const int timeoutMilliseconds = 120_000;
            while (true)
            {
                Task<string?> readLineTask = process.StandardOutput.ReadLineAsync();
                if (!readLineTask.Wait(timeoutMilliseconds))
                {
                    StopPaddleOcrWorker();
                    throw new TimeoutException("PaddleOCR 辨識逾時。");
                }

                string? line = readLineTask.Result;
                if (line is null)
                {
                    throw new InvalidOperationException("PaddleOCR 意外結束。請確認 Python 與 PaddleOCR 安裝狀態。");
                }

                try
                {
                    var response = JsonDocument.Parse(line);
                    if (response.RootElement.TryGetProperty("type", out var responseType) &&
                        (responseType.GetString() == expectedType || responseType.GetString() == "error"))
                    {
                        return response;
                    }
                    response.Dispose();
                }
                catch (JsonException)
                {
                    // PaddleOCR 的啟動訊息可能輸出到 stdout；略過非協定資料即可。
                }
            }
        }

        private static void StopPaddleOcrWorker()
        {
            lock (PaddleOcrLock)
            {
                if (paddleOcrWorker is null) return;

                try
                {
                    if (!paddleOcrWorker.HasExited)
                    {
                        paddleOcrWorker.StandardInput.WriteLine("{\"type\":\"shutdown\"}");
                        paddleOcrWorker.StandardInput.Flush();
                        if (!paddleOcrWorker.WaitForExit(2_000))
                        {
                            paddleOcrWorker.Kill(entireProcessTree: true);
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // The worker has already closed while the application is ending.
                }
                finally
                {
                    paddleOcrWorker.Dispose();
                    paddleOcrWorker = null;
                }
            }
        }

        private static IReadOnlyList<string> RecognizeTextWithMultipleLayouts(Pix image, string tessdataPath)
        {
            var pageSegmentationModes = new[]
            {
                PageSegMode.Auto,
                PageSegMode.SingleBlock,
                PageSegMode.SparseText
            };
            var results = new List<string>();

            // tessdata_best 是官方精度優先模型，較適合姓名中的罕見繁體字。
            using var engine = new TesseractEngine(tessdataPath, "chi_tra_best", EngineMode.Default);
            foreach (var mode in pageSegmentationModes)
            {
                string text;
                using (var page = engine.Process(image, mode))
                {
                    text = page.GetText();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add(text);
                }
            }

            return results;
        }

        private static string? FindMostLikelyPrescriptionDate(IEnumerable<string> recognizedTexts)
        {
            const string separatedRocDatePattern = @"(?<!\d)(?<year>1\d{2})\s*[.．﹒，,]\s*(?<month>0?[1-9]|1[0-2])\s*[.．﹒，,]\s*(?<day>0?[1-9]|[12]\d|3[01])(?!\d)";
            const string compactRocDatePattern = @"(?<!\d)(?<year>1\d{2})(?<month>0[1-9]|1[0-2])(?<day>0[1-9]|[12]\d|3[01])(?!\d)";

            var validDates = recognizedTexts
                .SelectMany(text => Regex.Matches(text, separatedRocDatePattern)
                    .Cast<Match>()
                    .Concat(Regex.Matches(text, compactRocDatePattern).Cast<Match>()))
                .Select(TryCreateRocDate)
                .Where(date => date.HasValue)
                .Select(date => date!.Value)
                .Where(date => date <= DateTime.Today.AddYears(1))
                .GroupBy(date => date)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key)
                .Select(group => group.Key)
                .FirstOrDefault();

            return validDates == default ? null : validDates.ToString("yyyy-MM-dd");
        }

        private static DateTime? TryCreateRocDate(Match match)
        {
            int year = int.Parse(match.Groups["year"].Value) + 1911;
            int month = int.Parse(match.Groups["month"].Value);
            int day = int.Parse(match.Groups["day"].Value);

            return day <= DateTime.DaysInMonth(year, month)
                ? new DateTime(year, month, day)
                : null;
        }

        private static IReadOnlyList<string> LoadPatientNames()
        {
            string patientFilePath = Path.Combine(AppContext.BaseDirectory, "patients.json");
            if (!File.Exists(patientFilePath)) return Array.Empty<string>();

            try
            {
                var entries = JsonSerializer.Deserialize<List<PatientEntry>>(File.ReadAllText(patientFilePath));
                if (entries is null) return Array.Empty<string>();

                return entries
                    .Select(entry => entry.Name.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (JsonException)
            {
                Console.WriteLine("病患名單格式無法讀取，姓名將維持待確認。");
                return Array.Empty<string>();
            }
        }

        private static string? FindOcrPatientName(IEnumerable<string> recognizedTexts)
        {
            // 藥單常在姓名後接病歷號或性別；只取得「姓名：」後第一個 2 至 4 字的中文姓名。
            const string namePattern = @"(?:姓名|姓\s*名)\s*[:：]?\s*(?:\d+\s*)?(?<name>[\u4E00-\u9FFF]{2,4})";
            return recognizedTexts
                .Select(text => Regex.Match(text, namePattern))
                .Where(match => match.Success)
                .Select(match => match.Groups["name"].Value)
                .FirstOrDefault();
        }

        private static List<MedicationModel> FindMedications(IEnumerable<string> recognizedTexts)
        {
            const string dosePattern = @"(?<![\d.．﹒，,])(?<amount>\d+(?:[.．﹒，,]\d+)?)\s*[.．﹒，,]?\s*(?<unit>[錠锭包粒片顆颗])";
            const string timingPattern = @"早餐後吃|午餐後吃|晚餐後吃|睡前|飯前|飯後";
            var medications = new List<MedicationModel>();
            var pendingNames = new List<string>();
            MedicationModel? current = null;

            foreach (string rawText in recognizedTexts)
            {
                string text = rawText.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                Match doseMatch = Regex.Match(text, dosePattern);
                if (doseMatch.Success)
                {
                    // Lightweight OCR often emits a complete table row as one line.
                    // Preserve the drug name before the dose instead of dropping it.
                    string inlineName = text[..doseMatch.Index].Trim();
                    inlineName = Regex.Replace(inlineName, @"\s*(?:內服|内服|外用)\s*$", "").Trim();
                    if (IsMedicationName(inlineName)) pendingNames.Add(inlineName);
                    string unit = doseMatch.Groups["unit"].Value.Replace('锭', '錠').Replace('颗', '顆');
                    current = new MedicationModel
                    {
                        Name = pendingNames.Count == 0 ? null : string.Join(" ", pendingNames),
                        Dose = $"{Regex.Replace(doseMatch.Groups["amount"].Value, "[．﹒，,]", ".")} {unit}"
                    };
                    medications.Add(current);
                    pendingNames.Clear();

                    Match usageMatch = Regex.Match(text,
                        @"[xX×*]\s*(?<times>\d+)\s*[xX×*]\s*(?<days>\d+)(?:\s+(?<total>\d+(?:[.．﹒，,]\d+)?))?");
                    if (usageMatch.Success)
                    {
                        current.Frequency = $"每日{usageMatch.Groups["times"].Value}次";
                        current.Days = int.Parse(usageMatch.Groups["days"].Value);
                        if (TryParseOcrDecimal(usageMatch.Groups["total"].Value, out decimal total))
                        {
                            current.TotalQuantity = total;
                        }
                    }

                    Match timingOnDoseLine = Regex.Match(text, timingPattern);
                    if (timingOnDoseLine.Success) current.Timing = timingOnDoseLine.Value;
                    continue;
                }

                Match timingMatch = Regex.Match(text, timingPattern);
                if (timingMatch.Success && current is not null)
                {
                    current.Timing = timingMatch.Value;
                    // 括號讓所有服用時間都必須接續天數，避免「早餐後吃」單獨符合後取得空字串。
                    Match daysOnTimingLine = Regex.Match(text, $@"(?:{timingPattern})\s*(?<days>\d{{1,3}})");
                    if (int.TryParse(daysOnTimingLine.Groups["days"].Value, out int daysOnTimingLineValue))
                    {
                        current.Days = daysOnTimingLineValue;
                    }
                    continue;
                }

                // 某些收據把天數獨立放在「早餐後吃」下一行，例如下一行只有 28。
                if (current is not null && current.Timing is not null &&
                    TryParseOcrDecimal(text, out decimal numericValue))
                {
                    if (current.Days is null && decimal.Truncate(numericValue) == numericValue &&
                        numericValue is > 0 and <= 365)
                    {
                        current.Days = (int)numericValue;
                    }
                    else if (current.TotalQuantity is null)
                    {
                        current.TotalQuantity = numericValue;
                    }
                    continue;
                }

                if (IsMedicationName(text))
                {
                    pendingNames.Add(text);
                    continue;
                }

                // 診斷、藥局資訊等文字代表一個藥品名稱區段已結束，不能誤併到下一項藥。
                if (IsNonMedicationDetail(text))
                {
                    pendingNames.Clear();
                    current = null;
                }
            }

            return medications
                .Where(medication => !string.IsNullOrWhiteSpace(medication.Name))
                .ToList();
        }

        private static bool TryParseOcrDecimal(string text, out decimal value)
        {
            string normalized = Regex.Replace(text.Trim(), "[．﹒，,]", ".");
            normalized = normalized.TrimEnd('.');
            return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsMedicationName(string text)
        {
            if (IsNonMedicationDetail(text) || Regex.IsMatch(text, "[：:]")) return false;
            return Regex.IsMatch(text, "[A-Za-z]") ||
                   Regex.IsMatch(text, "[\\u4E00-\\u9FFF]") &&
                   !Regex.IsMatch(text, @"掛費|自付|性別|卡序|藥品|明細|治療");
        }

        private static bool IsNonMedicationDetail(string text)
        {
            return Regex.IsMatch(text,
                @"院所|診所|地址|電話|姓名|身分證|生日|就診日|藥材|劑量|用法|總量|適應症|副作用|醫師|藥師|收據|本次|門診");
        }

        private static IReadOnlyList<PatientCandidate> FindPatientCandidates(string recognizedText, IReadOnlyList<string> patientNames)
        {
            string ocrCharacters = new string(recognizedText
                .Where(character => character is >= '\u4E00' and <= '\u9FFF')
                .ToArray());

            return patientNames
                .Select(name => new PatientCandidate(name, FindBestNameSimilarity(ocrCharacters, name)))
                .OrderByDescending(candidate => candidate.Similarity)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Take(3)
                .ToList();
        }

        private static int FindBestNameSimilarity(string ocrCharacters, string patientName)
        {
            if (string.IsNullOrWhiteSpace(patientName) || ocrCharacters.Length == 0) return 0;
            if (ocrCharacters.Contains(patientName, StringComparison.Ordinal)) return 100;

            int bestDistance = patientName.Length;
            for (int index = 0; index <= ocrCharacters.Length - patientName.Length; index++)
            {
                string fragment = ocrCharacters.Substring(index, patientName.Length);
                bestDistance = Math.Min(bestDistance, CalculateLevenshteinDistance(fragment, patientName));
            }

            return (int)Math.Round((1 - (double)bestDistance / patientName.Length) * 100);
        }

        private static int CalculateLevenshteinDistance(string left, string right)
        {
            var distances = new int[right.Length + 1];
            for (int column = 0; column <= right.Length; column++) distances[column] = column;

            for (int row = 1; row <= left.Length; row++)
            {
                int previousDiagonal = distances[0];
                distances[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int previousRow = distances[column];
                    int substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                    distances[column] = Math.Min(
                        Math.Min(distances[column] + 1, distances[column - 1] + 1),
                        previousDiagonal + substitutionCost);
                    previousDiagonal = previousRow;
                }
            }

            return distances[right.Length];
        }

        private static string? ConfirmPatient(string? ocrPatientName, IReadOnlyList<PatientCandidate> candidates)
        {
            if (Console.IsInputRedirected) return null;

            Console.WriteLine("\n姓名需要確認：");
            if (!string.IsNullOrWhiteSpace(ocrPatientName))
            {
                Console.WriteLine($"OCR 姓名：{ocrPatientName}");
            }
            if (candidates.Count == 0)
            {
                Console.WriteLine("病患名單沒有候選項目。");
            }
            else
            {
                for (int index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    Console.WriteLine($"{index + 1}. {candidate.Name}（OCR 相似度 {candidate.Similarity}%）");
                }
            }

            Console.Write("輸入編號確認；輸入 Y 採用 OCR 姓名；輸入 M 手動輸入；直接按 Enter 略過：");
            string? choice = Console.ReadLine()?.Trim();
            if (int.TryParse(choice, out int selected) && selected >= 1 && selected <= candidates.Count)
            {
                return candidates[selected - 1].Name;
            }

            if (string.Equals(choice, "Y", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ocrPatientName))
            {
                return ocrPatientName;
            }

            if (string.Equals(choice, "M", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("請輸入正確姓名：");
                string? manuallyEnteredName = Console.ReadLine()?.Trim();
                return string.IsNullOrWhiteSpace(manuallyEnteredName) ? null : manuallyEnteredName;
            }

            return null;
        }
    }
}

