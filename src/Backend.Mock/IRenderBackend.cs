namespace FairyNext.Backend.Mock;

/// <summary>
/// 渲染后端接口（设计书 §4.2）。v1 两实现：Mock（无 GPU，行为测试/参考光栅）与 WebGL2 顶点流；
/// Unity 段后端 M1 后半接入；StructuredBuffer 后端凭 uploadBytes 数据立项。
/// 契约要点：离屏 pass 必须先于主表面提交（v1.3）；缓冲回收走 fence 队列（v1.1）；
/// 零脏帧短路 + 空转收据适用于自绘后端（v1.2）。接口细化随 M1 实例流落地展开。
/// </summary>
public interface IRenderBackend
{
    string Name { get; }
    // M1: CreateStream / UploadInstances / UploadClips / WriteSlots / SetSegments / EndFrame(FrameStats)
}
