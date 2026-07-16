// NasServer/DTOs/MoveRequest.cs

namespace NasServer.DTOs;

/// <summary>
/// 移动 / 重命名请求。<paramref name="Source"/> 与 <paramref name="Dest"/> 均为用户根目录内的相对路径；
/// 由于传完整目标路径，"重命名"即把 Dest 设为同目录下的新名，"移动到其他目录"即把 Dest 设为目标目录下的同名。
/// <paramref name="Overwrite"/> 为 true 时允许覆盖已存在的目标。
/// </summary>
public record MoveRequest(string Source, string Dest, bool Overwrite = false);
