# 藥單 OCR 工作台

網頁已串接現有 C# / PaddleOCR，顯示實際辨識結果，不再使用固定示範資料。需要 .NET 10 SDK，以及已安裝 PaddleOCR 的 Python 環境。

## 啟動

在專案根目錄開啟 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\web\start.ps1
```

看到 `Now listening on: http://0.0.0.0:8080` 後，電腦開啟 http://localhost:8080 。腳本會建置後端至系統暫存目錄，並同時提供網頁與 API；請停止舊的靜態服務再啟動。按 Ctrl+C 停止。

指定其他連接埠：

```powershell
powershell -ExecutionPolicy Bypass -File .\web\start.ps1 -Port 8081
```

預設 OCR Python 是 `C:\ocr-tools\.venv313\Scripts\python.exe`。若安裝在其他位置，啟動前設定 `$env:PADDLE_OCR_PYTHON`。原本 `python web/serve.py` 現在也會轉接同一個啟動腳本。

## 手機操作

1. 電腦與手機連同一個區域網路，保持電腦及服務開啟。
2. 用 iPhone Safari 開啟啟動時列出的 LAN 網址，例如 `http://192.168.0.156:8080`；手機不能使用 localhost。
3. 選取圖片或點「拍攝藥單」，在系統選單選「拍照」。若相機畫面黑色，可先用相機 App 拍照，再選取照片。
4. 等待「OCR 模型已載入」後，設定藥盤編號（1～9999）及辨識模式，按「開始辨識」。預設原圖精細模式使用既有模型並送出原始檔；快速模式使用 PP-OCRv5 mobile 輕量模型，並將長邊超過 1600 px 的圖片等比例縮小後，以 JPEG 品質 95% 送出，不放大小圖。快速模式可能改變藥名或數字、造成欄位遺漏；結果有誤時請用原圖精細重試。拍攝時讓藥單文字盡量填滿畫面。
5. 查看實際姓名、日期、天數與藥品清單；未辨識到的欄位顯示「未辨識」，不會以示範值代替。請核對原始藥單。
6. 可展開辨識 JSON 並複製。區域網路 HTTP 可能禁止自動複製，失敗時會選取文字並提示手動複製。

支援 JPG、PNG、WebP，單張最多 20 MB；HEIC 請先轉成 JPG。同時只處理一張圖片，其他要求會收到忙碌提示，請稍後再試。每張圖片只應包含一位病患的藥單；若 OCR 偵測到多個「姓名：」欄位，會拒絕合併並提示裁切。此檢查依賴 OCR，不保證偵測到所有多張藥單，請先自行分開。

若手機無法連線，確認 Windows 防火牆允許 **dotnet** 在私人網路使用此 TCP 連接埠，並確認不是訪客 Wi-Fi、沒有 AP 裝置隔離或 VPN 干擾。此服務限可信任的區域網路使用，不要設定路由器對外轉發。

## API

- `GET /api/health`：服務存活狀態，不代表模型已載入。
- `POST /api/recognize`：`multipart/form-data`，單張檔案欄位 `image`，整數欄位 `tray`；可選 `mode` 為 `original` 或 `fast`，省略時為 `original`。
- 成功回傳現有 OCR 格式：`person`、`tray`、`dose`、`frequency`、`timing`、`startDate`、`days`、`medications`。
- 錯誤回傳 `{ "error": "說明" }`；格式錯誤 400、來源拒絕 403、圖片過大 413、多個姓名欄位 422、辨識忙碌 429、OCR 失敗 500、辨識逾時 504。
- 上傳暫存圖片在處理完成或失敗後刪除，結果不寫入資料庫；模型常駐於服務程序，後續辨識會沿用。
- 服務啟動時背景預載原圖模型，並用程式產生的文字圖片執行一次偵測與辨識預熱，完成後才回報 ready；啟動可能需要較長時間，預熱是提前執行初始化工作，不代表這段成本消失。`/api/health` 的 `model` 為 `loading`、`ready` 或 `unavailable`（表示啟動預載結果，實際請求失敗時仍會重試）。快速模型第一次使用時另行載入，後續沿用；尚未下載時，PaddleOCR 會從官方模型來源下載至本機快取。
- 頁面顯示送出圖片尺寸／大小，以及總時間、伺服器 OCR 時間與其餘時間。其餘時間含縮圖、傳輸、表單處理及回應等，不等同純上傳秒數。OCR 秒數可能包含模型尚未載入時的等待。
- `web` 的腳本、README 與後端來源不會由 HTTP 提供。
