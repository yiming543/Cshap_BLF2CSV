# 變更記錄

## 1.1.0 - 2026-11-03

- 新增 Vector BLF `CAN_FD_MESSAGE`（類型 100）解析。
- 支援 CAN 及 CAN FD 的 ID、資料長度、資料內容、時間間隔、方向、標準／擴展幀、資料／遠程幀、通道輸出。
- CAN CSV 格式與 `USB2CANFD&LIN Display List File` 範例一致。
- 保留原有 LIN CSV 輸出格式與原子檔案替換行為。
- BLF 無可轉換訊框或同時混合 CAN 與 LIN 時，顯示明確錯誤且不覆寫既有 CSV。
