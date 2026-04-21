namespace YobaConf.Core;

public interface IConfigStore
{
	HoconNode? FindNode(NodePath path);
}
