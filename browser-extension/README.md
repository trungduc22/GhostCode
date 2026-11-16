# AutoName Code Bridge — Browser Extension (Edge & Thorium)

## Cài đặt mới hoặc Cập nhật (Load Unpacked / Reload)

### Microsoft Edge (`msedge.exe`)
1. Mở trình duyệt Edge và truy cập `edge://extensions/`.
2. Bật công tắc **Developer mode** (Chế độ dành cho nhà phát triển).
3. Nếu chưa cài: bấm **Load unpacked** và chọn thư mục `C:\Users\Duc\Documents\ssh\browser-extension`.
4. **Nếu đã cài bản cũ trước đó**: bấm nút **Reload (⟳)** trên thẻ **AutoName Code Bridge (PTIT & Codeforces)** để kích hoạt Background Service Worker `v1.1.0` (nhận được cả các tab đã mở sẵn và vượt chặn Mixed Content HTTPS $\rightarrow$ `http://127.0.0.1:48123`).

### Thorium (`thorium.exe`)
1. Mở Thorium và truy cập `chrome://extensions/`.
2. Bật **Developer mode**.
3. Bấm **Load unpacked** (hoặc bấm **Reload ⟳** nếu đã cài trước đó) vào thư mục `C:\Users\Duc\Documents\ssh\browser-extension`.

## Cách hoạt động
- Hỗ trợ tự động: **Code PTIT (`code.ptit.edu.vn`)**, **Codeforces (`codeforces.com` — Problemset, Contest, Gym, Group, Edu, Submit)**, **VNOJ**, **LeetCode**, và **HackerRank**.
- Background Service Worker (`background.js`) tự đọc tab đang mở và gửi về app Windows qua `http://127.0.0.1:48123/context`, đồng thời `content.js` đọc tiêu đề bài từ DOM và đổi tiêu đề tab thành `[MÃ_BÀI] Tên bài`.
