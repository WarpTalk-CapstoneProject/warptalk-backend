import re

def process(filepath):
    with open(filepath, 'r') as f:
        content = f.read()
    
    content = re.sub(r'nameof\("([^"]+)"\)', r'"\1"', content)
    
    with open(filepath, 'w') as f:
        f.write(content)

process('./translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomParticipantService.cs')
process('./translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomService.cs')

