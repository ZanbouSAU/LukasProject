// NasServer/DTOs/DownloadTicketResponse.cs

namespace NasServer.DTOs;

/// <summary>下载票据。前端拿到后用普通 GET 直链下载，由浏览器原生接管（有进度、不占内存）。</summary>
public record DownloadTicketResponse(string Ticket);
