import os
import hashlib
import numpy as np
from typing import List, Dict, Any, Optional

class EmbeddingGemmaEngine:
    def __init__(self, model_name: str = "google/embeddinggemma-2", use_mock: Optional[bool] = None):
        self.model_name = model_name
        self.mock_mode = use_mock if use_mock is not None else os.getenv("MOCK_EMBEDDING", "0") == "1"
        self.device = "cpu"
        self.model = None

        if not self.mock_mode:
            try:
                import torch
                self.device = "cuda" if torch.cuda.is_available() else "cpu"
                from sentence_transformers import SentenceTransformer
                print(f"[EmbeddingEngine] Loading model '{self.model_name}' on {self.device}...")
                self.model = SentenceTransformer(self.model_name, device=self.device)
                print(f"[EmbeddingEngine] Model loaded successfully.")
            except Exception as ex:
                print(f"[EmbeddingEngine] Warning: Could not load real model ({ex}). Falling back to mock engine.")
                self.mock_mode = True

    def _generate_mock_vector(self, seed_str: str, dim: int = 768) -> List[float]:
        """Generates a deterministic normalized pseudo-embedding vector for testing."""
        h = hashlib.sha256(seed_str.encode('utf-8')).digest()
        # Seed numpy with hash integer
        seed_int = int.from_bytes(h[:4], byteorder='big')
        rng = np.random.default_rng(seed_int)
        vec = rng.standard_normal(dim).astype(np.float32)
        norm = np.linalg.norm(vec)
        if norm > 0:
            vec /= norm
        return vec.tolist()

    def encode_query(self, query: str, modality: str = "text", truncate_dim: int = 768) -> List[float]:
        """Encodes a single search query (text or image path) into a normalized embedding."""
        if self.mock_mode:
            return self._generate_mock_vector(f"query:{modality}:{query}", dim=truncate_dim)

        if modality == "text":
            try:
                # Use standard EmbeddingGemma 2 SearchQuery prompt
                vec = self.model.encode(
                    query,
                    prompt_name="SearchQuery",
                    truncate_dim=truncate_dim,
                    normalize_embeddings=True
                )
            except Exception:
                # Fallback to manual instruction format if prompt_name is not configured in local checkpoint
                vec = self.model.encode(
                    f"task: search result | query: {query}",
                    truncate_dim=truncate_dim,
                    normalize_embeddings=True
                )
            return vec.tolist()
        elif modality == "image":
            # Cross-modal visual search query
            vec = self.model.encode(
                {"image": query},
                truncate_dim=truncate_dim,
                normalize_embeddings=True
            )
            return vec.tolist()
        elif modality == "audio":
            vec = self.model.encode(
                {"audio": query},
                truncate_dim=truncate_dim,
                normalize_embeddings=True
            )
            return vec.tolist()
        else:
            raise ValueError(f"Unsupported query modality: {modality}")

    def encode_batch(self, items: List[Dict[str, Any]], truncate_dim: int = 768) -> List[List[float]]:
        """Encodes multiple items (text chunks, image files, audio files, video segments)."""
        if self.mock_mode:
            results = []
            for item in items:
                m_type = item.get("type", "text")
                seed = item.get("text") or item.get("path") or str(item)
                results.append(self._generate_mock_vector(f"{m_type}:{seed}", dim=truncate_dim))
            return results

        prepared_inputs = []
        is_text_list = []

        for item in items:
            m_type = item.get("type", "text")
            if m_type == "text":
                title = item.get("title", "").strip()
                content = item.get("text", "").strip()
                if title:
                    formatted_text = f"title: {title} | text: {content}"
                else:
                    formatted_text = f"title: none | text: {content}"
                prepared_inputs.append(formatted_text)
                is_text_list.append(True)
            elif m_type == "image":
                prepared_inputs.append({"image": item["path"]})
                is_text_list.append(False)
            elif m_type == "audio":
                prepared_inputs.append({"audio": item["path"]})
                is_text_list.append(False)
            elif m_type == "video_segment":
                # If video path is provided
                if "video_path" in item and os.path.exists(item["video_path"]):
                    prepared_inputs.append({"video": item["video_path"]})
                elif "thumbnail_path" in item and os.path.exists(item["thumbnail_path"]):
                    # Fallback to keyframe visual encoding
                    prepared_inputs.append({"image": item["thumbnail_path"]})
                else:
                    prepared_inputs.append({"text": item.get("text", "video segment")})
                is_text_list.append(False)
            else:
                raise ValueError(f"Unknown item type: {m_type}")

        # Compute embeddings
        # For mixed or multimodal inputs, we encode with truncate_dim & normalize
        embeddings = self.model.encode(
            prepared_inputs,
            truncate_dim=truncate_dim,
            normalize_embeddings=True
        )

        if isinstance(embeddings, np.ndarray):
            return embeddings.tolist()
        return [e.tolist() for e in embeddings]
