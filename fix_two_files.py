import os
import re

def process_file(filepath):
    with open(filepath, 'r') as f:
        content = f.read()
        
    content = re.sub(r'\bRoomStatus\.([A-Z_]+)\b(?!\.ToString)', r'"\1"', content)
    content = re.sub(r'\bTranslationRoomParticipantStatus\.([A-Z_]+)\b(?!\.ToString)', r'"\1"', content)
    content = re.sub(r'\bArtifactType\.([A-Z_]+)\b(?!\.ToString)', r'"\1"', content)
    
    with open(filepath, 'w') as f:
        f.write(content)

process_file('./translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomParticipantService.cs')
process_file('./translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomService.cs')

