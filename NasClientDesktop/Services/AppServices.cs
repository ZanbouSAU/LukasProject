// NasClientDesktop/Services/AppServices.cs
// 极简服务容器（手动组合，AOT 友好，无反射 DI）。
// 单例贯穿应用生命周期，由 App 在启动时创建。

namespace NasClientDesktop.Services;

public sealed class AppServices
{
    public TokenStore Tokens { get; }
    public HttpService Http { get; }
    public AuthService Auth { get; }
    public FileService Files { get; }

    public AppServices()
    {
        Tokens = new TokenStore();
        Http = new HttpService(Tokens);
        Auth = new AuthService(Http);
        Files = new FileService(Http);
    }
}
