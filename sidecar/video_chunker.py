import os
import subprocess
from typing import List, Dict, Any, Optional

def extract_video_segments_ffmpeg(
    video_path: str,
    output_dir: str,
    segment_duration_sec: int = 15,
    overlap_sec: int = 3
) -> List[Dict[str, Any]]:
    """
    Extracts video segments and keyframe thumbnails using ffmpeg if available,
    or generates metadata chunks with timestamp boundaries.
    """
    os.makedirs(output_dir, exist_ok=True)
    base_name = os.path.splitext(os.path.basename(video_path))[0]

    # Attempt to probe video duration with ffprobe
    duration_sec = 0.0
    try:
        cmd = [
            "ffprobe", "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", video_path
        ]
        res = subprocess.run(cmd, capture_output=True, text=True, check=True)
        duration_sec = float(res.stdout.strip())
    except Exception:
        # Fallback duration estimation if ffprobe not available
        duration_sec = 60.0

    segments = []
    current_start = 0.0
    step = segment_duration_sec - overlap_sec
    if step <= 0:
        step = segment_duration_sec

    idx = 0
    while current_start < duration_sec:
        current_end = min(current_start + segment_duration_sec, duration_sec)
        start_ms = int(current_start * 1000)
        end_ms = int(current_end * 1000)

        # Attempt to capture a thumbnail at mid-point using ffmpeg
        thumb_name = f"{base_name}_seg_{idx:04d}.jpg"
        thumb_path = os.path.join(output_dir, thumb_name)
        mid_sec = (current_start + current_end) / 2.0

        try:
            cmd = [
                "ffmpeg", "-y", "-ss", str(mid_sec), "-i", video_path,
                "-vframes", "1", "-q:v", "2", thumb_path
            ]
            subprocess.run(cmd, capture_output=True, check=True)
        except Exception:
            # If ffmpeg extraction failed, thumb_path can be None
            thumb_path = ""

        segments.append({
            "segment_index": idx,
            "start_ms": start_ms,
            "end_ms": end_ms,
            "thumbnail_path": thumb_path if os.path.exists(thumb_path) else None,
            "video_path": video_path
        })

        idx += 1
        current_start += step
        if current_start >= duration_sec:
            break

    return segments
