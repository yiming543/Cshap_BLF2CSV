# BLF 批次轉 CSV

Windows 桌面程式，可選擇一個目錄，逐一將目錄最上層的所有 `.blf` 檔轉成同名 `.csv` 檔，並在完成後顯示轉換結果。

可直接執行 `publish\BLF批次轉CSV.exe`。執行電腦需安裝 .NET 9 Desktop Runtime，且 `publish` 內的檔案需放在同一個資料夾。

## 使用方式

1. 執行程式並按「選擇目錄」。
2. 選擇包含 BLF 檔案的資料夾。
3. 按「開始轉換」。
4. 輸出檔會放在原目錄，例如 `sample.blf` 會輸出為 `sample.csv`。

若同名 CSV 已存在，成功完成轉換後會覆寫。轉換中若發生錯誤，原 CSV 不會被半成品取代。

## 建置

```powershell
dotnet build .\BlfToCsv.sln -c Release
```
