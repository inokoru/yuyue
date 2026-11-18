"""把声纹模型（3D-Speaker 的 CAM++，中文通用版，Apache-2.0）转成 App 用的 Core ML 和 ONNX。

用法：<装了 torch==2.5.1、coremltools==8.3、onnx、onnxruntime 的 python> export_speaker.py <campplus_cn_common.bin> <输出文件夹>

campplus_cn_common.bin 是 ModelScope 上 iic/speech_campplus_sv_zh-cn_16k-common 的权重。
下面的模型结构移植自 3D-Speaker 的 speakerlab/models/campplus（只留推理用到的部分）：
Copyright 3D-Speaker (https://github.com/alibaba-damo-academy/3D-Speaker)，Apache License 2.0。
输入：fbank [1, 帧, 80]（核心 `speaker_features` 做的 Kaldi 式 fbank，减过均值）；
输出：embedding [1, 192]。
- SpeakerPrint.mlmodelc.zip：给 Mac、iPhone（半精度，帧数 50–6000）。
- speaker_print.onnx：给 Android，输入名同样是 fbank。
两份和原始 PyTorch 在随机输入上比余弦，都要 > 0.999。
"""
import os
import shutil
import subprocess
import sys
import tempfile
from collections import OrderedDict

import numpy as np
import torch
import torch.nn.functional as F
from torch import nn


def nonlinear(config, channels):
    seq = nn.Sequential()
    for name in config.split("-"):
        if name == "relu":
            seq.add_module("relu", nn.ReLU(inplace=True))
        elif name == "batchnorm":
            seq.add_module("batchnorm", nn.BatchNorm1d(channels))
        elif name == "batchnorm_":
            seq.add_module("batchnorm", nn.BatchNorm1d(channels, affine=False))
    return seq


class StatsPool(nn.Module):
    def forward(self, x):
        return torch.cat([x.mean(dim=2), x.std(dim=2, unbiased=True)], dim=1)


class TDNNLayer(nn.Module):
    def __init__(self, cin, cout, k, stride=1, padding=0, dilation=1, bias=False, config="batchnorm-relu"):
        super().__init__()
        if padding < 0:
            padding = (k - 1) // 2 * dilation
        self.linear = nn.Conv1d(cin, cout, k, stride=stride, padding=padding, dilation=dilation, bias=bias)
        self.nonlinear = nonlinear(config, cout)

    def forward(self, x):
        return self.nonlinear(self.linear(x))


class CAMLayer(nn.Module):
    def __init__(self, bn, cout, k, stride, padding, dilation, bias, reduction=2):
        super().__init__()
        self.linear_local = nn.Conv1d(bn, cout, k, stride=stride, padding=padding, dilation=dilation, bias=bias)
        self.linear1 = nn.Conv1d(bn, bn // reduction, 1)
        self.relu = nn.ReLU(inplace=True)
        self.linear2 = nn.Conv1d(bn // reduction, cout, 1)
        self.sigmoid = nn.Sigmoid()

    def forward(self, x):
        y = self.linear_local(x)
        context = x.mean(-1, keepdim=True) + self.seg_pooling(x)
        m = self.sigmoid(self.linear2(self.relu(self.linear1(context))))
        return y * m

    @staticmethod
    def seg_pooling(x, seg_len=100):
        # Each 100-frame segment's mean, spread back over its frames.
        # The last segment is short; converters differ on what it's divided by (its frames or
        # the full 100), so it's divided by the same pooling of ones, right either way.
        seg = F.avg_pool1d(x, kernel_size=seg_len, stride=seg_len, ceil_mode=True)
        ones = F.avg_pool1d(torch.ones_like(x[:, :1, :]), kernel_size=seg_len, stride=seg_len, ceil_mode=True)
        seg = torch.repeat_interleave(seg / ones, seg_len, dim=-1)
        return seg[..., : x.shape[-1]]


class CAMDenseTDNNLayer(nn.Module):
    def __init__(self, cin, cout, bn, k, dilation=1, config="batchnorm-relu"):
        super().__init__()
        padding = (k - 1) // 2 * dilation
        self.nonlinear1 = nonlinear(config, cin)
        self.linear1 = nn.Conv1d(cin, bn, 1, bias=False)
        self.nonlinear2 = nonlinear(config, bn)
        self.cam_layer = CAMLayer(bn, cout, k, stride=1, padding=padding, dilation=dilation, bias=False)

    def forward(self, x):
        return self.cam_layer(self.nonlinear2(self.linear1(self.nonlinear1(x))))


class CAMDenseTDNNBlock(nn.ModuleList):
    def __init__(self, layers, cin, cout, bn, k, dilation):
        super().__init__()
        for i in range(layers):
            self.add_module("tdnnd%d" % (i + 1), CAMDenseTDNNLayer(cin + i * cout, cout, bn, k, dilation))

    def forward(self, x):
        for layer in self:
            x = torch.cat([x, layer(x)], dim=1)
        return x


class TransitLayer(nn.Module):
    def __init__(self, cin, cout, bias=True, config="batchnorm-relu"):
        super().__init__()
        self.nonlinear = nonlinear(config, cin)
        self.linear = nn.Conv1d(cin, cout, 1, bias=bias)

    def forward(self, x):
        return self.linear(self.nonlinear(x))


class DenseLayer(nn.Module):
    def __init__(self, cin, cout, config):
        super().__init__()
        self.linear = nn.Conv1d(cin, cout, 1, bias=False)
        self.nonlinear = nonlinear(config, cout)

    def forward(self, x):
        return self.nonlinear(self.linear(x.unsqueeze(-1)).squeeze(-1))


class BasicResBlock(nn.Module):
    def __init__(self, cin, planes, stride=1):
        super().__init__()
        self.conv1 = nn.Conv2d(cin, planes, 3, stride=(stride, 1), padding=1, bias=False)
        self.bn1 = nn.BatchNorm2d(planes)
        self.conv2 = nn.Conv2d(planes, planes, 3, stride=1, padding=1, bias=False)
        self.bn2 = nn.BatchNorm2d(planes)
        self.shortcut = nn.Sequential()
        if stride != 1 or cin != planes:
            self.shortcut = nn.Sequential(nn.Conv2d(cin, planes, 1, stride=(stride, 1), bias=False), nn.BatchNorm2d(planes))

    def forward(self, x):
        out = F.relu(self.bn1(self.conv1(x)))
        out = self.bn2(self.conv2(out)) + self.shortcut(x)
        return F.relu(out)


class FCM(nn.Module):
    def __init__(self, m=32, feat=80):
        super().__init__()
        self.conv1 = nn.Conv2d(1, m, 3, stride=1, padding=1, bias=False)
        self.bn1 = nn.BatchNorm2d(m)
        self.layer1 = nn.Sequential(BasicResBlock(m, m, 2), BasicResBlock(m, m, 1))
        self.layer2 = nn.Sequential(BasicResBlock(m, m, 2), BasicResBlock(m, m, 1))
        self.conv2 = nn.Conv2d(m, m, 3, stride=(2, 1), padding=1, bias=False)
        self.bn2 = nn.BatchNorm2d(m)
        self.out_channels = m * (feat // 8)

    def forward(self, x):
        out = F.relu(self.bn1(self.conv1(x.unsqueeze(1))))
        out = self.layer2(self.layer1(out))
        out = F.relu(self.bn2(self.conv2(out)))
        return out.flatten(1, 2)


class CAMPPlus(nn.Module):
    def __init__(self, feat=80, embedding=192, growth=32, bn_size=4, init=128):
        super().__init__()
        self.head = FCM(feat=feat)
        c = self.head.out_channels
        self.xvector = nn.Sequential(OrderedDict([("tdnn", TDNNLayer(c, init, 5, stride=2, padding=-1))]))
        c = init
        for i, (n, k, d) in enumerate(zip((12, 24, 16), (3, 3, 3), (1, 2, 2))):
            self.xvector.add_module("block%d" % (i + 1), CAMDenseTDNNBlock(n, c, growth, bn_size * growth, k, d))
            c += n * growth
            self.xvector.add_module("transit%d" % (i + 1), TransitLayer(c, c // 2, bias=False))
            c //= 2
        self.xvector.add_module("out_nonlinear", nonlinear("batchnorm-relu", c))
        self.xvector.add_module("stats", StatsPool())
        self.xvector.add_module("dense", DenseLayer(c * 2, embedding, "batchnorm_"))

    def forward(self, x):
        return self.xvector(self.head(x.permute(0, 2, 1)))


def cos(a, b):
    a, b = a.reshape(-1), b.reshape(-1)
    return float(np.dot(a, b) / np.linalg.norm(a) / np.linalg.norm(b))


def main():
    import coremltools as ct
    import onnxruntime as ort

    weights, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    model = CAMPPlus()
    model.load_state_dict(torch.load(weights, map_location="cpu", weights_only=True))
    model.eval()

    example = torch.randn(1, 300, 80)
    onnx_path = os.path.join(out, "speaker_print.onnx")
    torch.onnx.export(model, example, onnx_path, input_names=["fbank"], output_names=["embedding"],
                      dynamic_axes={"fbank": {1: "frames"}}, opset_version=13)

    traced = torch.jit.trace(model, example)
    ml = ct.convert(traced,
                    inputs=[ct.TensorType(name="fbank", shape=(1, ct.RangeDim(50, 6000, default=300), 80), dtype=np.float32)],
                    outputs=[ct.TensorType(name="embedding", dtype=np.float32)],
                    minimum_deployment_target=ct.target.iOS17, compute_precision=ct.precision.FLOAT16)
    ml.short_description = "CAM++ speaker embedding (3D-Speaker, zh-cn 16k common), Apache-2.0"

    ort_s = ort.InferenceSession(onnx_path)
    for frames in (60, 250, 301, 1500):
        x = np.random.randn(1, frames, 80).astype(np.float32)
        with torch.no_grad():
            ref = model(torch.from_numpy(x)).numpy()
        o = ort_s.run(None, {"fbank": x})[0]
        c = ml.predict({"fbank": x})["embedding"]
        print(f"{frames} 帧：onnx {cos(ref, o):.5f}  coreml {cos(ref, c):.5f}")
        assert cos(ref, o) > 0.999 and cos(ref, c) > 0.999

    with tempfile.TemporaryDirectory() as tmp:
        pkg = os.path.join(tmp, "SpeakerPrint.mlpackage")
        ml.save(pkg)
        subprocess.run(["xcrun", "coremlcompiler", "compile", pkg, tmp], check=True, stdout=subprocess.DEVNULL)
        zip_path = os.path.join(out, "SpeakerPrint.mlmodelc.zip")
        if os.path.exists(zip_path):
            os.remove(zip_path)
        subprocess.run(["ditto", "-c", "-k", "--norsrc", "--noextattr", "--noqtn", "--keepParent", os.path.join(tmp, "SpeakerPrint.mlmodelc"), zip_path], check=True)
    for f in ("SpeakerPrint.mlmodelc.zip", "speaker_print.onnx"):
        print(f, os.path.getsize(os.path.join(out, f)))


if __name__ == "__main__":
    main()
