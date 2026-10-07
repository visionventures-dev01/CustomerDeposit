# Lens Voucher (C#) – วิธีนำขึ้นเครื่องทดสอบ

## 1) Build
```
dotnet publish -c Release -o publish
```
ได้โฟลเดอร์ `publish` (ประมาณ 19 MB, view ถูกคอมไพล์ล่วงหน้าแล้ว) ต้องมี **.NET 8 ASP.NET Core Hosting Bundle** บนเครื่องปลายทาง

## 2) ตั้งค่า (แนะนำให้ใช้ environment variable หรือ `appsettings.Production.json` ในโฟลเดอร์ publish — ห้าม commit ความลับ)

| คีย์ | ความหมาย |
|---|---|
| `ConnectionStrings__VOUCHER_ConnectionString` | MSSQL (ต้องอ่าน view `vvd_voucher_*` ได้) |
| `Smsmkt__ApiKey` / `Smsmkt__SecretKey` / `Smsmkt__ProjectKey` | SMSMKT สำหรับส่ง OTP จริง **ถ้าไม่ตั้ง ระบบจะส่ง OTP ไม่ได้** (ตั้งใจ) |
| `Admin__AdAuthUrl` | URL ของ AD gateway สำหรับหน้า `/Admin` |
| `PathBase` | เช่น `/lens-voucher` ถ้าอยู่หลัง IIS/ARR ที่ path นี้ |
| `DataProtectionPath` | โฟลเดอร์เก็บ key เข้ารหัส cookie (เช่น `D:/apps/csharp/lens-keys`) ถ้าไม่ตั้ง การรีสตาร์ทจะทำให้ทุกคนต้อง login ใหม่ |
| `CookieSecure` | `true` เมื่อใช้ HTTPS |
| `AppVersion`, `BuildCommit`, `BuildTime` | แสดงที่ badge / `/health` / `/version` |
| `Pdf__FontPath`, `Pdf__BoldFontPath` | ปกติไม่ต้องตั้ง (ใช้ Tahoma ใน `C:\Windows\Fonts`) |

**ห้ามตั้ง `Otp__DemoCode` บน production** (รหัสตายตัวใช้สำหรับ Development เท่านั้น)

## 3) รัน
- ทดสอบ: `dotnet CustomerDepositRemaining.dll --urls http://127.0.0.1:8300`
- ใช้งานจริง: ลงเป็น Windows Service (`sc create` / NSSM) หรือ IIS (ASP.NET Core Module) แล้วให้ IIS/ARR ชี้มาที่พอร์ตนี้

## 4) ตรวจหลังขึ้น
- `GET /health` ต้องได้ `{"status":"ok",...}`
- เข้า `/Account/Login` → กรอกเบอร์ → ได้ OTP จริง → เข้าหน้า Voucher
- เปิดลิงก์ PDF ในหน้า Redemption History

## หมายเหตุ
- ไฟล์ template เดิม (Sneat/Kendo ใน `wwwroot/assets`, `kendo2020` ฯลฯ) ไม่ได้ใช้ และถูกตัดออกจาก publish อัตโนมัติ
- session/OTP เก็บในหน่วยความจำ รันได้ 1 instance (ถ้ารีสตาร์ท ผู้ใช้ต้อง login ใหม่)
