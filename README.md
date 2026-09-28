# nebb.dev

## PixPeek Dev Manager

PixPeek의 Dev Manager를 별도 Windows WPF 프로젝트로 복사한 첫 단계입니다.
PixPeek의 기존 서버 실행, 워크트리 표시, Git 작업 동작을 유지합니다.
이 앱을 빌드하고 실행하는 데 PixPeek 소스 트리 안의 Dev Manager 프로젝트는 필요하지 않습니다.

Windows에서 .NET 10 SDK를 설치한 뒤 nebb.dev 폴더에서 실행합니다.

```powershell
dotnet run --project src/Nebb.DevManager/Nebb.DevManager.csproj
```

처음 실행할 때 PixPeek 저장소의 루트 폴더를 선택합니다. 선택한 경로는
`%LOCALAPPDATA%\Nebb\DevManager\pixpeek-repository.txt`에 저장되어 다음 실행에 사용됩니다.
다른 PixPeek 저장소 경로를 지정하려면 다음과 같이 실행합니다.

```powershell
dotnet run --project src/Nebb.DevManager/Nebb.DevManager.csproj -- --repository "C:\path\to\PixPeek"
```

앱의 서버 실행 기능에는 선택한 PixPeek 저장소와 기존 개발 도구
(Git, Node.js/npm, Chrome)가 필요합니다. 기존 PixPeek Dev Manager와 같은 단일 실행
잠금 및 `%LOCALAPPDATA%\PixPeek\DevManager`의 실행 기록을 사용하므로, 두 앱이
동시에 같은 PixPeek 서버를 제어하지 않습니다. 새 앱을 사용하려면 기존 앱을 닫으세요.
