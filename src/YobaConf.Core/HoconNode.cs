namespace YobaConf.Core;

public sealed record HoconNode(NodePath Path, string RawContent, DateTimeOffset UpdatedAt);
