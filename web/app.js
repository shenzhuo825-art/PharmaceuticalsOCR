"use strict";
const $ = (id) => document.getElementById(id);
let currentResult = null;
let selectedFile = null;
let busy = false;
let imageUrl = null;
let selectionVersion = 0;
function message(text, error = false) {
  $("message").textContent = text;
  $("message").classList.toggle("error", error);
}
function clearResult() {
  currentResult = null;
  $("result").hidden = true;
  $("empty-result").hidden = false;
  $("copy-status").textContent = "";
  document.querySelector("details").open = false;
}
function clearImage() {
  if (imageUrl) URL.revokeObjectURL(imageUrl);
  imageUrl = null;
  selectedFile = null;
  $("preview").removeAttribute("src");
  $("preview").hidden = true;
  $("placeholder").hidden = false;
  $("file-name").textContent = "尚未選取圖片";
  $("image-info").textContent = "";
}
function selectFile(file) {
  if (!file || busy) return;
  const version = ++selectionVersion;
  clearResult();
  clearImage();
  if (!["image/jpeg", "image/png", "image/webp"].includes(file.type)) {
    message("不支援此檔案，請選取 JPG、PNG 或 WebP 圖片。若為 iPhone HEIC 照片，請先轉成 JPG 再選取。", true);
    return;
  }
  if (file.size > 20 * 1024 * 1024) {
    message("圖片不可超過 20 MB。", true); return;
  }
  const url = URL.createObjectURL(file);
  imageUrl = url;
  const probe = new Image();
  probe.onload = () => {
    if (version !== selectionVersion) return;
    selectedFile = file;
    $("preview").src = url;
    $("preview").hidden = false;
    $("placeholder").hidden = true;
    $("file-name").textContent = file.name;
    $("image-info").textContent = `原始圖片：${probe.naturalWidth} × ${probe.naturalHeight}，${(file.size / 1024 / 1024).toFixed(2)} MB`;
    message("圖片已就緒，點「開始辨識」後會上傳至本機電腦。");
  };
  probe.onerror = () => {
    if (version !== selectionVersion) return;
    clearImage();
    message("無法讀取此圖片，請重新選取有效的圖片檔案。", true);
  };
  probe.src = url;
}
for (const [button, input] of [["choose", "file"], ["camera", "camera-file"]]) {
  $(button).addEventListener("click", () => $(input).click());
  $(input).addEventListener("change", (event) => {
    selectFile(event.target.files[0]);
    event.target.value = "";
  });
}
$("reset").addEventListener("click", () => {
  ++selectionVersion;
  clearImage(); clearResult();
  message("請先選取一張藥單圖片。");
});
function displayValue(value) {
  return value === null || value === undefined || value === "" ? "未辨識" : String(value);
}
function renderResult(data) {
  currentResult = data;
  $("person-value").textContent = displayValue(data.person);
  $("tray-value").textContent = displayValue(data.tray);
  $("date-value").textContent = displayValue(data.startDate);
  $("days-value").textContent = data.days == null ? "未辨識" : `${data.days} 天`;
  const medications = Array.isArray(data.medications) ? data.medications : [];
  $("medication-count").textContent = `${medications.length} 項`;
  $("medications").replaceChildren();
  if (!medications.length) $("medications").textContent = "未辨識到藥品，請確認圖片清晰並核對原始藥單。";
  medications.forEach((medication, index) => {
    const row = document.createElement("div"); row.className = "medication";
    const number = document.createElement("span"); number.className = "med-index"; number.textContent = String(index + 1).padStart(2, "0");
    const content = document.createElement("div");
    const title = document.createElement("h4"); title.textContent = displayValue(medication.name);
    const usage = document.createElement("p"); usage.textContent = `劑量：${displayValue(medication.dose)} · 頻率：${displayValue(medication.frequency)} · 時機：${displayValue(medication.timing)}`;
    const detail = document.createElement("small"); detail.textContent = `天數：${displayValue(medication.days)} · 總數量：${displayValue(medication.totalQuantity)}`;
    content.append(title, usage, detail); row.append(number, content); $("medications").append(row);
  });
  $("json").textContent = JSON.stringify(data, null, 2);
  $("empty-result").hidden = true; $("result").hidden = false;
}
async function prepareUpload(file) {
  const image = $("preview");
  const width = image.naturalWidth, height = image.naturalHeight;
  const scale = $("mode").value === "fast" ? Math.min(1, 1600 / Math.max(width, height)) : 1;
  if (scale === 1) return { file, description: `原圖 ${width} × ${height}` };
  // Use the already decoded preview so iPhone photo orientation matches the preview.
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width * scale));
  canvas.height = Math.max(1, Math.round(height * scale));
  try {
    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("無法準備快速圖片，請改用原圖精細。");
    ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = "high";
    ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.95));
    if (!blob) throw new Error("無法準備快速圖片，請改用原圖精細。");
    return { file: new File([blob], "prescription.jpg", { type: "image/jpeg" }),
      description: `快速 ${canvas.width} × ${canvas.height}` };
  } finally { canvas.width = 0; canvas.height = 0; }
}

async function checkModel() {
  try {
    const response = await fetch("/api/health", { cache: "no-store" });
    if (!response.ok) throw new Error();
    const data = await response.json();
    $("model-status").textContent = data.model === "ready" ? "OCR 模型已載入，可開始辨識。"
      : data.model === "loading" ? "OCR 模型預先載入中，完成後可減少首次等待。" : "OCR 模型未就緒，辨識時會重試載入。";
    if (data.model === "loading") setTimeout(checkModel, 3000);
  } catch { $("model-status").textContent = "無法確認 OCR 狀態，請確認電腦服務已啟動。"; }
}
checkModel();

$("recognize").addEventListener("click", async () => {
  if (busy) return;
  if (!selectedFile || $("preview").hidden) { message("請先選取一張可預覽的藥單圖片。", true); return; }
  if (!$("tray").reportValidity()) return;
  clearResult(); busy = true;
  for (const id of ["choose", "camera", "reset", "recognize", "tray", "mode"]) $(id).disabled = true;
  $("recognize").textContent = "辨識中…";
  const started = performance.now();
  const update = () => message(`處理中，已等待 ${Math.floor((performance.now() - started) / 1000)} 秒。請保持頁面開啟。`);
  update(); const timer = setInterval(update, 1000);
  try {
    const upload = await prepareUpload(selectedFile);
    $("image-info").textContent = `本次送出：${upload.description}，${(upload.file.size / 1024 / 1024).toFixed(2)} MB`;
    const form = new FormData(); form.append("image", upload.file); form.append("tray", $("tray").value);
    form.append("mode", $("mode").value);
    const response = await fetch("/api/recognize", { method: "POST", body: form });
    const type = response.headers.get("content-type") || "";
    if (!type.includes("application/json")) throw new Error("未連到 OCR API，請用新版 web/start.ps1 啟動服務。");
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || `辨識失敗（${response.status}）。`);
    if (!data || !Array.isArray(data.medications)) throw new Error("伺服器回傳的辨識格式無效。");
    renderResult(data);
    clearInterval(timer);
    const total = (performance.now() - started) / 1000;
    const match = /(?:^|,)\s*ocr;dur=([\d.]+)/.exec(response.headers.get("server-timing") || "");
    const ocr = match ? Number(match[1]) / 1000 : null;
    const timing = ocr === null ? "" : `（OCR ${ocr.toFixed(1)} 秒；圖片處理／傳輸等 ${Math.max(0, total - ocr).toFixed(1)} 秒）`;
    message(`辨識完成，總耗時 ${total.toFixed(1)} 秒${timing}。請核對原始藥單。`);
  } catch (error) {
    clearInterval(timer);
    message(error instanceof TypeError ? "無法連接 OCR 服務，請確認電腦服務及網路連線。" : error.message, true);
  } finally {
    clearInterval(timer); busy = false;
    for (const id of ["choose", "camera", "reset", "recognize", "tray", "mode"]) $(id).disabled = false;
    $("recognize").textContent = "開始辨識 →";
  }
});
$("copy").addEventListener("click", async () => {
  const version = selectionVersion;
  if (!currentResult) return;
  const text = JSON.stringify(currentResult, null, 2);
  try {
    if (!navigator.clipboard?.writeText) throw new Error("clipboard unavailable");
    await navigator.clipboard.writeText(text);
    if (version === selectionVersion) $("copy-status").textContent = "已複製辨識 JSON。";
  } catch {
    if (version !== selectionVersion) return;
    const range = document.createRange();
    range.selectNodeContents($("json"));
    const selection = window.getSelection();
    selection.removeAllRanges(); selection.addRange(range);
    $("json").focus();
    $("copy-status").textContent = "瀏覽器無法自動複製。已選取 JSON，請長按或使用 Ctrl+C 手動複製。";
  }
});
const zone = $("drop-zone");
for (const eventName of ["dragenter", "dragover"]) {
  zone.addEventListener(eventName, (event) => { event.preventDefault(); zone.classList.add("dragging"); });
}
zone.addEventListener("dragleave", () => zone.classList.remove("dragging"));
zone.addEventListener("drop", (event) => {
  event.preventDefault(); zone.classList.remove("dragging");
  selectFile(event.dataTransfer.files[0]);
});
window.addEventListener("beforeunload", () => { if (imageUrl) URL.revokeObjectURL(imageUrl); });
