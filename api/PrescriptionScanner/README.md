# OCR 執行與效能

## 執行

需要 .NET 10 與已安裝 PaddleOCR 的 Python。預設 Python 為 `C:\ocr-tools\.venv313\Scripts\python.exe`，可用 `PADDLE_OCR_PYTHON` 環境變數覆寫。

在儲存庫根目錄執行單張或多張圖片：

```powershell
dotnet run --project api/PrescriptionScanner -- "C:\images\first.jpg" "C:\images\second.jpg"
```

多張圖片在同一程序依序處理，共用常駐 Python worker，模型只載入一次。第一張包含 Python 匯入與模型載入時間；每張圖片印出處理耗時。單張失敗會顯示錯誤並繼續下一張，程序結束碼為 1。不帶參數維持原本的預設測試圖片。

## 效能調整

- 移除每次 OCR 的暫存 JSON 檔案讀寫，使用 PaddleOCR 公開的 `result.json["res"]`；保留完整結果格式。
- 預設維持 10 個 CPU 執行緒、原有模型、原圖及辨識參數，不以縮圖或替換模型換取速度。
- 若要在其他電腦調整，可設定 `PADDLE_OCR_CPU_THREADS` 為正整數；只有 worker 啟動時讀取，修改後需重啟程序。
- 此電腦初步測試：原始版本三張圖片約 28.99、27.99、41.09 秒（不含匯入及模型載入，合計另外約 11.46 秒）。1 與 4 執行緒在第一張分別約 58.55、76.47 秒，均未採用。數字為單次量測，不能視為穩定效能保證。
- 暫存檔移除主要減少額外 I/O；單張推論仍是主要耗時。連續多張省去的是重複啟動成本，不會省略每張的 OCR。

## 重現測試

```powershell
C:\ocr-tools\.venv313\Scripts\python.exe api/PrescriptionScanner/tools/benchmark_ocr.py api/PrescriptionScanner/S__36208690.jpg api/PrescriptionScanner/S__36249618.jpg api/PrescriptionScanner/prescription.png.jpg --threads 10 4 --repeats 2
```

工具列出初始化與辨識耗時，並逐次比較文字是否與第一個設定相同；不輸出或儲存病人文字。不同執行緒數未必較快，請以目標機器實測。結果一致僅代表測試圖片的回歸比較，不能代替其他藥單的正確率驗證。

目前 `web` 仍為獨立示範介面，尚未串接本程式。
