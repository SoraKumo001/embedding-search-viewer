import os
import fitz  # PyMuPDF
from fastapi import FastAPI, HTTPException, Body
from pydantic import BaseModel, Field
from typing import List, Optional, Dict, Any
from embedding_engine import EmbeddingGemmaEngine

app = FastAPI(title="EmbeddingGemma 2 Multimodal Search Sidecar", version="0.1.0")

# Engine instance initialized on startup
engine: Optional[EmbeddingGemmaEngine] = None

@app.on_event("startup")
def startup_event():
    global engine
    model_name = os.getenv("EMBEDDING_MODEL_NAME", "google/embeddinggemma-2")
    engine = EmbeddingGemmaEngine(model_name=model_name)

class HealthResponse(BaseModel):
    status: str
    model: str
    device: str
    mock_mode: bool
    supported_dimensions: List[int]

class QueryRequest(BaseModel):
    query: str
    modality: str = Field(default="text", description="'text', 'image', or 'audio'")
    truncate_dim: int = Field(default=768, description="768, 512, 256, or 128")

class QueryResponse(BaseModel):
    embedding: List[float]
    dimension: int

class BatchItem(BaseModel):
    type: str = Field(..., description="'text', 'image', 'audio', or 'video_segment'")
    text: Optional[str] = None
    title: Optional[str] = None
    path: Optional[str] = None
    video_path: Optional[str] = None
    thumbnail_path: Optional[str] = None
    start_ms: Optional[int] = None
    end_ms: Optional[int] = None

class BatchRequest(BaseModel):
    items: List[BatchItem]
    truncate_dim: int = Field(default=768, description="768, 512, 256, or 128")

class BatchResponse(BaseModel):
    embeddings: List[List[float]]
    count: int
    dimension: int

class PdfPageExtractRequest(BaseModel):
    pdf_path: str
    output_dir: str

class PdfPageExtractItem(BaseModel):
    page_number: int
    image_path: str
    text_content: str

class PdfPageExtractResponse(BaseModel):
    total_pages: int
    pages: List[PdfPageExtractItem]

@app.get("/health", response_model=HealthResponse)
def get_health():
    global engine
    if engine is None:
        return HealthResponse(
            status="initializing",
            model="unknown",
            device="unknown",
            mock_mode=True,
            supported_dimensions=[768, 512, 256, 128]
        )
    return HealthResponse(
        status="ready",
        model=engine.model_name,
        device=engine.device,
        mock_mode=engine.mock_mode,
        supported_dimensions=[768, 512, 256, 128]
    )

@app.post("/embed/query", response_model=QueryResponse)
def embed_query(req: QueryRequest):
    global engine
    if engine is None:
        raise HTTPException(status_code=503, detail="Embedding engine not initialized")
    try:
        vec = engine.encode_query(req.query, modality=req.modality, truncate_dim=req.truncate_dim)
        return QueryResponse(embedding=vec, dimension=len(vec))
    except Exception as ex:
        raise HTTPException(status_code=500, detail=str(ex))

@app.post("/embed/batch", response_model=BatchResponse)
def embed_batch(req: BatchRequest):
    global engine
    if engine is None:
        raise HTTPException(status_code=503, detail="Embedding engine not initialized")
    try:
        items_dict = [item.dict(exclude_none=True) for item in req.items]
        vectors = engine.encode_batch(items_dict, truncate_dim=req.truncate_dim)
        return BatchResponse(
            embeddings=vectors,
            count=len(vectors),
            dimension=req.truncate_dim
        )
    except Exception as ex:
        raise HTTPException(status_code=500, detail=str(ex))

@app.post("/media/extract-pdf-pages", response_model=PdfPageExtractResponse)
def extract_pdf_pages(req: PdfPageExtractRequest):
    """Extracts text and page images using PyMuPDF (fitz) for reliable rendering."""
    if not os.path.exists(req.pdf_path):
        raise HTTPException(status_code=404, detail="PDF file not found")
    os.makedirs(req.output_dir, exist_ok=True)

    try:
        doc = fitz.open(req.pdf_path)
        pages_result = []
        for i, page in enumerate(doc):
            page_num = i + 1
            text = page.get_text("text").strip()
            # Render page to image (150 DPI)
            pix = page.get_pixmap(dpi=150)
            img_path = os.path.join(req.output_dir, f"page_{page_num:04d}.png")
            pix.save(img_path)
            pages_result.append(PdfPageExtractItem(
                page_number=page_num,
                image_path=img_path,
                text_content=text
            ))
        doc.close()
        return PdfPageExtractResponse(total_pages=len(pages_result), pages=pages_result)
    except Exception as ex:
        raise HTTPException(status_code=500, detail=f"PDF extraction failed: {ex}")

class VideoSegmentExtractRequest(BaseModel):
    video_path: str
    output_dir: str
    segment_duration_sec: int = 15
    overlap_sec: int = 3

class VideoSegmentItem(BaseModel):
    segment_index: int
    start_ms: int
    end_ms: int
    thumbnail_path: Optional[str] = None
    video_path: str

class VideoSegmentExtractResponse(BaseModel):
    total_segments: int
    segments: List[VideoSegmentItem]

@app.post("/media/extract-video-segments", response_model=VideoSegmentExtractResponse)
def extract_video_segments_api(req: VideoSegmentExtractRequest):
    if not os.path.exists(req.video_path):
        raise HTTPException(status_code=404, detail="Video file not found")
    try:
        from video_chunker import extract_video_segments_ffmpeg
        segs = extract_video_segments_ffmpeg(
            req.video_path,
            req.output_dir,
            segment_duration_sec=req.segment_duration_sec,
            overlap_sec=req.overlap_sec
        )
        items = [VideoSegmentItem(**s) for s in segs]
        return VideoSegmentExtractResponse(total_segments=len(items), segments=items)
    except Exception as ex:
        raise HTTPException(status_code=500, detail=f"Video segment extraction failed: {ex}")

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", "5000"))
    uvicorn.run("server:app", host="127.0.0.1", port=port, log_level="info")
