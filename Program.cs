using CustomerDepositRemaining.Controllers;
using CustomerDepositRemaining.Services;
using CustomerDepositRemaining.Services.Pdf;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);

// ค่าลับของเครื่องนั้น ๆ (connection string, คีย์ SMSMKT ฯลฯ) ใส่ใน appsettings.Local.json ซึ่งไม่ถูกเก็บใน git
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

var mvc = builder.Services.AddControllersWithViews();
#if DEBUG
mvc.AddRazorRuntimeCompilation();
#endif

builder.Services.Configure<SmsmktOptions>(builder.Configuration.GetSection("Smsmkt"));
builder.Services.Configure<OtpOptions>(builder.Configuration.GetSection("Otp"));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<PdfOptions>(builder.Configuration.GetSection("Pdf"));
// ให้ Razor ส่งภาษาไทยออกเป็นตัวอักษรจริง (ไม่ใช่ &#xE23; ทุกตัว)
builder.Services.Configure<WebEncoderOptions>(o =>
    o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Thai));
builder.Services.AddMemoryCache();

// เก็บ key เข้ารหัส cookie ไว้ในโฟลเดอร์ (ถ้าตั้งค่า) เพื่อให้ session ไม่หลุดเมื่อ service รีสตาร์ท
var keysPath = builder.Configuration["DataProtectionPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("CustomerDepositRemaining")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}
builder.Services.AddHttpClient<IOtpService, OtpService>();
builder.Services.AddHttpClient<IAdminAuthService, AdminAuthService>();
builder.Services.AddScoped<IVoucherLookupService, VoucherLookupService>();
builder.Services.AddScoped<IDocumentPdfService, DocumentPdfService>();
builder.Services.AddSingleton<PdfRenderer>();

var cookieSecure = builder.Configuration.GetValue<bool>("CookieSecure")
    ? CookieSecurePolicy.Always
    : CookieSecurePolicy.SameAsRequest;

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "lens_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = cookieSecure;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
    })
    .AddCookie(AdminController.Scheme, o =>
    {
        o.Cookie.Name = "lens_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = cookieSecure;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.LoginPath = "/Admin";
        o.AccessDeniedPath = "/Admin";
    });
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SecurePolicy = cookieSecure;
});

// IIS/ARR reverse proxy: ใช้ X-Forwarded-* ที่ proxy ส่งมา (proxy อยู่ในเครื่องเดียวกัน/เครือข่ายภายใน)
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

// เช่น Hosting ใต้ https://service.vvretail.co.th/lens-voucher ตั้ง "PathBase": "/lens-voucher"
var pathBase = builder.Configuration["PathBase"]?.Trim();
if (!string.IsNullOrEmpty(pathBase) && pathBase != "/")
{
    app.UsePathBase("/" + pathBase.Trim('/'));
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
