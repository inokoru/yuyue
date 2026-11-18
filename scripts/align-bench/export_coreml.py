"""把强制对齐模型（Meta MMS，torchaudio 的 MMS_FA）转成 App 用的 Core ML 和 ONNX。

用法：<装了 torch==2.5.1、torchaudio==2.5.1、coremltools==8.3、onnx 的 python> export_coreml.py <model.pt> <输出文件夹>

model.pt 是 MMS_FA 的权重（torchaudio 下载的那份；官方下载地址不通时用 Hugging Face 上的同一份）。
输出：
- MMSAligner.mlmodelc.zip：输入 wav [1, 544000]（34 秒 16 kHz，App 自己做 layer norm），
  输出 emissions [1, 帧, 28]（每帧 28 个字母的对数概率，顺序同 forced::LETTERS），8 位权重。
- mms_fa.int8.onnx：同样的输入输出，长度可变，给 Android。
"""
import os
import shutil
import subprocess
import sys
import tempfile

import coremltools as ct
import coremltools.optimize.coreml as cto
import torch
import torchaudio

WINDOW = 34 * 16000
weights, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)

bundle = torchaudio.pipelines.MMS_FA
params = dict(bundle._params)
params["aux_num_out"] = 31
model = torchaudio.models.wav2vec2_model(**params)
model.load_state_dict(torch.load(weights, weights_only=True, map_location="cpu"))
# Leave out the three tokens the aligner doesn't use (as torchaudio does): 28 letters remain.
keep = [i for i in range(31) if i not in bundle._remove_aux_axis]
aux = torch.nn.Linear(1024, len(keep))
aux.weight.data = model.aux.weight.data[keep]
aux.bias.data = model.aux.bias.data[keep]
model.aux = aux
model.eval()
assert bundle.get_labels(star=None) == tuple("-aienoutsrmkldghybpwcvjzf'qx")


class Aligner(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, wav):
        x, _ = self.m(wav)
        return torch.log_softmax(x, dim=-1)


net = Aligner(model).eval()
example = torch.randn(1, WINDOW)
with torch.no_grad():
    traced = torch.jit.trace(net, example)
    reference = net(example)

ml = ct.convert(
    traced,
    inputs=[ct.TensorType(name="wav", shape=(1, WINDOW))],
    outputs=[ct.TensorType(name="emissions")],
    compute_precision=ct.precision.FLOAT16,
    minimum_deployment_target=ct.target.iOS18,
)
ml = cto.linear_quantize_weights(ml, cto.OptimizationConfig(global_config=cto.OpLinearQuantizerConfig(mode="linear_symmetric")))
got = ml.predict({"wav": example.numpy()})["emissions"]
print("Core ML vs torch, mean abs diff of log probs:", float(abs(torch.tensor(got) - reference).mean()))

with tempfile.TemporaryDirectory() as tmp:
    pkg = os.path.join(tmp, "MMSAligner.mlpackage")
    ml.save(pkg)
    subprocess.run(["xcrun", "coremlcompiler", "compile", pkg, tmp], check=True, stdout=subprocess.DEVNULL)
    zip_to = os.path.join(out, "MMSAligner.mlmodelc.zip")
    if os.path.exists(zip_to):
        os.remove(zip_to)
    # The zip holds the folder MMSAligner.mlmodelc at its top.
    subprocess.run(["ditto", "-c", "-k", "--keepParent", os.path.join(tmp, "MMSAligner.mlmodelc"), zip_to], check=True)
print("wrote", os.path.join(out, "MMSAligner.mlmodelc.zip"))

if "--onnx" in sys.argv:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    with tempfile.TemporaryDirectory() as tmp:
        plain = os.path.join(tmp, "mms_fa.onnx")
        torch.onnx.export(net, example, plain, input_names=["wav"], output_names=["emissions"], dynamic_axes={"wav": {1: "samples"}, "emissions": {1: "frames"}}, opset_version=17)
        quantize_dynamic(plain, os.path.join(out, "mms_fa.int8.onnx"), weight_type=QuantType.QInt8)
    print("wrote", os.path.join(out, "mms_fa.int8.onnx"))
