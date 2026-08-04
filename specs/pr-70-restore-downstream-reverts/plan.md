# Plan: Restore downstream files reverted in PR #70

## Mục tiêu

Loại bỏ khỏi PR #70 các thay đổi không thuộc scope chính bằng cách restore lại những file thuộc
`gateway/` và `translation-room/` về đúng trạng thái hiện có trên `origin/development`.

Sau khi commit và push patch restore này, PR #70 sẽ không còn hiển thị các file `gateway/` và
`translation-room/` trong tab **Files changed**, miễn là nội dung các file đó khớp hoàn toàn với
`origin/development`.

## Vấn đề hiện tại

Commit `727ce5d chore: stabilize workspace settings update flow` trong PR #70 đã thay đổi lại các
file vốn thuộc những PR/ticket khác đã merge vào `development`, gồm:

- WT-187: realtime refresh khi Translation Room invitation thay đổi.
- WT-188: workspace Owner/Admin có thể admit participant trong Translation Room.
- WT-191: realtime relay khi room ended.

Vì `development` đã chứa các behavior đó, nhưng PR #70 lại làm các file khác đi, GitHub xem đây là
diff của PR #70 và hiển thị chúng trong **Files changed**.

## File cần restore

Gateway:

- `gateway/src/WarpTalk.Gateway/Services/NotificationRedisSubscriberService.cs`
- `gateway/src/WarpTalk.Gateway/Services/TranslationRoomRedisSubscriberService.cs`
- `gateway/tests/WarpTalk.Gateway.Tests/NotificationRedisSubscriberServiceTests.cs`

Translation Room:

- `translation-room/src/WarpTalk.TranslationRoomService.API/Program.cs`
- `translation-room/src/WarpTalk.TranslationRoomService.API/appsettings.json`
- `translation-room/src/WarpTalk.TranslationRoomService.Application/Interfaces/IWorkspaceMemberDirectory.cs`
- `translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomParticipantService.cs`
- `translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomService.cs`
- `translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/Clients/WorkspaceMemberGrpcDirectory.cs`
- `translation-room/tests/WarpTalk.TranslationRoomService.Tests/Application/Services/LanguageConfigurationTests.cs`
- `translation-room/tests/WarpTalk.TranslationRoomService.Tests/Application/Services/ParticipantManagementServiceTests.cs`
- `translation-room/tests/WarpTalk.TranslationRoomService.Tests/Application/Services/TranslationRoomServiceTests.cs`

## Cách thực hiện

1. Restore đúng các path trên từ `origin/development`.

   ```powershell
   git restore --source=origin/development -- gateway translation-room
   ```

2. Kiểm tra diff còn lại so với `development`.

   ```powershell
   git diff --name-status origin/development...HEAD -- gateway translation-room
   ```

   Sau khi commit restore, command này phải không còn output.

3. Stage đúng các file restore, không stage file khác.

   ```powershell
   git add -- gateway translation-room
   git diff --cached --name-status
   ```

4. Commit riêng.

   ```powershell
   git commit -m "restore downstream realtime service changes"
   ```

5. Push lên branch PR #70.

   ```powershell
   git push origin chore/update-auto-save-settings-pages
   ```

## Kết quả mong đợi

Sau khi push:

- PR #70 không còn hiển thị `gateway/` trong **Files changed**.
- PR #70 không còn hiển thị `translation-room/` trong **Files changed**.
- Các behavior đã merge từ WT-187, WT-188, WT-191 vẫn được giữ nguyên theo `development`.
- PR #70 chỉ còn các thay đổi thật sự thuộc scope Workspace/settings và các commit restore cần thiết.

## Lưu ý khi commit

Không commit các file tài liệu tạm hoặc phân tích nội bộ nếu không muốn chúng xuất hiện trong PR body/diff.
Hiện file `specs/pr-70-workspace-flow-analysis.md` đang là untracked local file và không nên stage nếu mục tiêu chỉ là restore code.
