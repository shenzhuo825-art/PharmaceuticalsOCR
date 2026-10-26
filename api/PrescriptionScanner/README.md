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
- OCR 核心預設維持 10 個 CPU 執行緒、原有模型及辨識參數。網頁提供原圖精細與快速（PP-OCRv5_mobile_det / PP-OCRv5_mobile_rec，加上瀏覽器縮圖）兩種模式；兩組模型在同一 Python worker 內分別常駐，避免切換模式重啟程序。命令列預設仍按傳入的原圖辨識，Python 單張工具可使用 `--profile fast`。
- 若要在其他電腦調整，可設定 `PADDLE_OCR_CPU_THREADS` 為正整數；只有 worker 啟動時讀取，修改後需重啟程序。
- 此電腦初步測試：原始版本三張圖片約 28.99、27.99、41.09 秒（不含匯入及模型載入，合計另外約 11.46 秒）。1 與 4 執行緒在第一張分別約 58.55、76.47 秒，均未採用。數字為單次量測，不能視為穩定效能保證。
- 暫存檔移除主要減少額外 I/O；單張推論仍是主要耗時。連續多張省去的是重複啟動成本，不會省略每張的 OCR。
- 網頁啟動時會預載原圖模型，並用合成文字圖片預熱偵測與辨識網路，再回報 ready。首次推論的額外成本移到啟動階段，仍需等待預熱完成。快速模式第一次使用時載入輕量模型。測試發現模式及縮圖會改變部分藥名／數字，因此仍以原圖精細為預設。
- 2026-10-26 網頁端同一張測試圖 `S__36249618.jpg`：原圖模式約 25.6 秒、快速模式約 22.1 秒（單次量測）。使用者另提供的 960 × 1706 照片：原圖約 33.8 秒、縮至 1600 後約 35.5 秒。小幅縮圖不一定較快，不能承諾每張都有改善。
- 修正 ASCII 小數劑量解析，避免 `1.00 錠` 被截成 `00 錠`。偵測到多個姓名標籤時拒絕當成單一病患回傳，避免藥品混合；該檢查仍依賴 OCR 是否正確辨識到姓名標籤。
- 另測試使用者提供的同張照片，輕量模型原圖 OCR 約 24 秒；API 整圖約 26.6 秒後回傳多姓名 422。只裁下方單一病患文字區域後，快速 API 約 14.8 秒，可回傳兩項藥品及兩筆 `1.00 錠` 劑量；仍有藥名文字誤讀與總量缺漏，不能當作精確率保證。裁切與模型切換都有影響，不能把它視為同條件的整圖加速倍數。
- 調整解析器以接收「藥名與劑量同一行」的 OCR 輸出，並支援 `锭`／`颗` 單位正規化。執行六個無個資回歸測試：`dotnet run --project api/PrescriptionScanner.Checks --artifacts-path <測試輸出目錄>`。

## 重現測試

```powershell
C:\ocr-tools\.venv313\Scripts\python.exe api/PrescriptionScanner/tools/benchmark_ocr.py api/PrescriptionScanner/S__36208690.jpg api/PrescriptionScanner/S__36249618.jpg api/PrescriptionScanner/prescription.png.jpg --threads 10 4 --repeats 2
```

工具列出初始化與辨識耗時，並逐次比較文字是否與第一個設定相同；不輸出或儲存病人文字。不同執行緒數未必較快，請以目標機器實測。結果一致僅代表測試圖片的回歸比較，不能代替其他藥單的正確率驗證。

`web` 已透過 `POST /api/recognize` 串接本程式。用 `web/start.ps1` 同時啟動網頁與 API；命令列多張處理方式維持不變。服務模式使用 `--serve --urls http://0.0.0.0:8080 --web-root <web絕對路徑>`。
