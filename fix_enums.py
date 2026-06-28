import os
import re

def fix_file(filepath):
    with open(filepath, 'r') as f:
        content = f.read()
    
    # We want to replace RoomStatus.[A-Z_]+ with RoomStatus.[A-Z_]+.ToString()
    # BUT we should be careful not to replace it if it already has .ToString()
    # Also, be careful with Enum.TryParse
    
    # Let's just use string literals instead. RoomStatus.SCHEDULED -> "SCHEDULED"
    content = re.sub(r'RoomStatus\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)
    content = re.sub(r'ParticipantStatus\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)
    content = re.sub(r'TranslationRoomParticipantStatus\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)
    content = re.sub(r'ArtifactType\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)
    
    content = re.sub(r'TranscriptStatus\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)
    content = re.sub(r'CorrectionStatus\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)
    content = re.sub(r'CorrectionType\.([A-Z_]+)(?!\.ToString\(\))', r'"\1"', content)

    # Note: If there are Enums like Enum.TryParse<RoomStatus>(...), changing RoomStatus to string might cause errors.
    # Let's see if we need to fix those manually.
    
    with open(filepath, 'w') as f:
        f.write(content)

for root, dirs, files in os.walk('.'):
    for file in files:
        if file.endswith('.cs'):
            fix_file(os.path.join(root, file))
