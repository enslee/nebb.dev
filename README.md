# nebb.dev

## Dev Manager (Windows)

여러 로컬 Git 저장소의 워크트리와 브랜치 상태를 한 화면에서 관리하는 WPF 앱입니다.
PixPeek의 기존 서버 실행 기능도 유지합니다.

Windows에서 .NET 10 SDK를 설치한 뒤 nebb.dev 폴더에서 실행합니다.

```powershell
dotnet run --project src/Nebb.DevManager/Nebb.DevManager.csproj
```

상단 **저장소 추가**에서 기존 로컬 Git 저장소 폴더를 선택합니다. 워크트리
폴더를 선택해도 같은 저장소로 인식하며 중복 등록하지 않습니다. 등록 목록은
`%LOCALAPPDATA%\Nebb\DevManager\repositories.json`에 저장됩니다. 이전 버전에서
선택한 PixPeek 경로는 처음 실행할 때 목록으로 가져옵니다.

상단 드롭다운에서 **모든 저장소**를 선택하면 등록한 모든 저장소의 워크트리를
표시하고, 저장소 하나를 선택하면 그 저장소의 워크트리만 표시합니다. 목록의
**저장소** 열로 브랜치의 소속을 구분합니다. **원격 갱신**은 현재 선택한 저장소에
적용하며, **모든 저장소**를 선택했을 때는 전체에 적용합니다.

시작할 때 저장소를 추가하고 해당 저장소를 선택하려면 다음과 같이 실행합니다.

```powershell
dotnet run --project src/Nebb.DevManager/Nebb.DevManager.csproj -- --repository "C:\path\to\repository"
```

각 저장소의 Git 상태·커밋·푸시·병합은 해당 저장소에서 실행됩니다. 기준 브랜치는
`origin/HEAD`가 가리키는 브랜치이며, 설정되지 않았다면 `main` 또는 `master`를
사용합니다. 원격 작업에는 `origin`이 필요합니다.

**실행/전환**, **종료**, **웹 열기**, **리빌드 후 재시작**은 PixPeek 저장소에서만
사용할 수 있습니다. 다른 프로젝트의 실행 설정은 아직 제공하지 않습니다.
PixPeek 서버 실행에는 Node.js/npm과 Chrome이 필요합니다. 기존 PixPeek Dev
Manager와 같은 단일 실행 잠금 및 `%LOCALAPPDATA%\PixPeek\DevManager`의 실행
기록을 사용하므로, 새 앱을 사용하려면 기존 앱을 닫으세요.
