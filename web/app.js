"use strict";
const $ = (id) => document.getElementById(id);
const sample = {
  person: "示範使用者", tray: 2, dose: "1", frequency: "每日3次", timing: "飯後",
  startDate: "2026-10-23", days: 3,
  medications: [
    { name: "示範藥品 A", dose: "1 錠", timing: "飯後", frequency: "每日3次", days: 3, totalQuantity: 9 },
    { name: "示範藥品 B", dose: "1 錠", timing: "睡前", frequency: "每日1次", days: 3, totalQuantity: 3 }
  ]
};
let imageUrl = null;
let selectionVersion = 0;
function message(text, error = false) {
  $("message").textContent = text;
  $("message").classList.toggle("error", error);
}
function clearResult() {
  $("result").hidden = true;
  $("empty-result").hidden = false;
  $("copy-status").textContent = "";
  document.querySelector("details").open = false;
}
function clearImage() {
  if (imageUrl) URL.revokeObjectURL(imageUrl);
  imageUrl = null;
  $("preview").removeAttribute("src");
  $("preview").hidden = true;
  $("placeholder").hidden = false;
  $("file-name").textContent = "尚未選取圖片";
}
function selectFile(file) {
  if (!file) return;
  const version = ++selectionVersion;
  clearResult();
  clearImage();
  if (!["image/jpeg", "image/png", "image/webp"].includes(file.type)) {
    message("不支援此檔案，請選取 JPG、PNG 或 WebP 圖片。若為 iPhone HEIC 照片，請先轉成 JPG 再選取。", true);
    return;
  }
  const url = URL.createObjectURL(file);
  imageUrl = url;
  const probe = new Image();
  probe.onload = () => {
    if (version !== selectionVersion) return;
    $("preview").src = url;
    $("preview").hidden = false;
    $("placeholder").hidden = true;
    $("file-name").textContent = file.name;
    message("圖片已就緒，可顯示示範辨識結果。圖片不會上傳。");
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
$("recognize").addEventListener("click", () => {
  if ($("preview").hidden) {
    message("請先選取一張可預覽的藥單圖片。", true);
    return;
  }
  $("json").textContent = JSON.stringify(sample, null, 2);
  $("empty-result").hidden = true;
  $("result").hidden = false;
  message("已顯示固定示範資料，並非此圖片的實際辨識結果。");
});
$("copy").addEventListener("click", async () => {
  const version = selectionVersion;
  const text = JSON.stringify(sample, null, 2);
  try {
    if (!navigator.clipboard?.writeText) throw new Error("clipboard unavailable");
    await navigator.clipboard.writeText(text);
    if (version === selectionVersion) $("copy-status").textContent = "已複製示範 JSON。";
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
