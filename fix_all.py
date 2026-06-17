import os
import re

def process_file(filepath):
    with open(filepath, 'r') as f:
        content = f.read()
        
    original = content
    
    # 1. Remove MapEnum in Program.cs
    if "Program.cs" in filepath:
        content = re.sub(r'dataSourceBuilder\.MapEnum<[^>]+>\("[^"]+"\);\n\s*', '', content)
        
    # 2. Remove HasPostgresEnum in DbContext.partial.cs
    if "DbContext.partial.cs" in filepath or "DbContext.Custom.cs" in filepath:
        content = re.sub(r'modelBuilder\.HasPostgresEnum<[^>]+>\("[^"]+",\s*"[^"]+"\);\n\s*', '', content)
        content = re.sub(r'\.HasDefaultValue\(TranscriptStatus\.[a-zA-Z]+\)', '.HasDefaultValue("RECORDING")', content)
        content = re.sub(r'\.HasDefaultValue\(CorrectionStatus\.[a-zA-Z]+\)', '.HasDefaultValue("PENDING")', content)

    # 3. Application layer - Replace Enum usages with String usages
    # e.g., RoomStatus.SCHEDULED -> "SCHEDULED"
    content = re.sub(r'\bRoomStatus\.([A-Z_]+)\b(?!\.ToString)', r'"\1"', content)
    content = re.sub(r'\bTranslationRoomParticipantStatus\.([A-Z_]+)\b(?!\.ToString)', r'"\1"', content)
    content = re.sub(r'\bArtifactType\.([A-Z_]+)\b(?!\.ToString)', r'"\1"', content)
    
    content = re.sub(r'\bTranscriptStatus\.([A-Z][a-zA-Z_]+)\b(?!\.ToString)', lambda m: f'"{m.group(1).upper()}"', content)
    content = re.sub(r'\bCorrectionStatus\.([A-Z][a-zA-Z_]+)\b(?!\.ToString)', lambda m: f'"{m.group(1).upper()}"', content)
    content = re.sub(r'\bCorrectionType\.([A-Z][a-zA-Z_]+)\b(?!\.ToString)', lambda m: f'"{m.group(1).upper()}"', content)

    # Note: Transcript uses PascalCase enum values like TranscriptStatus.Recording
    # That's why we uppercase them with lambda: "{m.group(1).upper()}"
    
    # 4. Enum.TryParse -> Remove or adapt?
    # Enum.TryParse<RoomStatus>(s, true, out var parsedStatus)
    # If the logic parses Enum, we can just use the string directly if they are strings now!
    # For now let's leave Enum.TryParse and fix manually if needed.
    
    if content != original:
        with open(filepath, 'w') as f:
            f.write(content)

for root, dirs, files in os.walk('.'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
