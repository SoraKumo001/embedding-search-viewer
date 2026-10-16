import sys
import os
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
import torch
from pathlib import Path
from sentence_transformers import SentenceTransformer
from PIL import Image

output_dir = Path("../models")
output_dir.mkdir(parents=True, exist_ok=True)

print("Loading SentenceTransformer model for Vision ONNX export...")
model = SentenceTransformer("google/embeddinggemma-2")

proc = model[0].processor
inner_model = model[0].auto_model

vision_onnx_path = output_dir / "embeddinggemma2_vision.onnx"
print(f"Exporting vision encoder to {vision_onnx_path}...")

# Create dummy input from a blank image
dummy_img = Image.new("RGB", (224, 224), color="white")
dummy_inputs = proc(images=dummy_img, return_tensors="pt")

class VisionWrapper(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids, attention_mask, pixel_values, image_position_ids):
        outputs = self.model(
            input_ids=input_ids,
            attention_mask=attention_mask,
            pixel_values=pixel_values,
            image_position_ids=image_position_ids
        )
        last_hidden_state = outputs.last_hidden_state
        input_mask_expanded = attention_mask.unsqueeze(-1).expand(last_hidden_state.size()).float()
        sum_embeddings = torch.sum(last_hidden_state * input_mask_expanded, 1)
        sum_mask = torch.clamp(input_mask_expanded.sum(1), min=1e-9)
        mean_pooled = sum_embeddings / sum_mask
        normalized = torch.nn.functional.normalize(mean_pooled, p=2, dim=1)
        return normalized

vision_wrapper = VisionWrapper(inner_model)
vision_wrapper.eval()

args = (
    dummy_inputs["input_ids"],
    dummy_inputs["attention_mask"],
    dummy_inputs["pixel_values"],
    dummy_inputs["image_position_ids"]
)

with torch.no_grad():
    torch.onnx.export(
        vision_wrapper,
        args,
        str(vision_onnx_path),
        input_names=["input_ids", "attention_mask", "pixel_values", "image_position_ids"],
        output_names=["embedding"],
        dynamic_axes={
            "input_ids": {0: "batch_size"},
            "attention_mask": {0: "batch_size"},
            "pixel_values": {0: "batch_size"},
            "image_position_ids": {0: "batch_size"},
            "embedding": {0: "batch_size"}
        },
        opset_version=18,
        do_constant_folding=True
    )

print(f"Vision encoder exported successfully! File size: {vision_onnx_path.stat().st_size / (1024*1024):.2f} MB")
