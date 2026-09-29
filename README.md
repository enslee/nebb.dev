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

PixPeek 저장소의 **실행/전환**, **종료**, **웹 열기**, **리빌드 후 재시작**은 기존 방식으로
동작합니다. 다른 저장소에서는 워크트리를 선택한 뒤 **명령 설정**에서
실행·테스트 명령 후보를 확인하고 각각 하나를 선택하거나 직접 추가할 수 있습니다.
후보를 선택한 뒤 이름, 명령, 인수, 워크트리 기준 상대 작업 폴더, 환경변수를
수정하세요. 환경변수는 한 줄에 `NAME=value` 형식으로 입력합니다.
후보는 프로젝트 폴더별로 묶어 표시하며 Docker Compose 서비스는 **Services**에
별도로 표시합니다. 서비스는 여러 개를 선택하고 각각 수정·저장할 수 있습니다.
선택한 워크트리 안에서 다른 `.git` 저장소나 워크트리를 만나면 그 내부는 탐색하지
않으며 `.claude/worktrees`의 복사본도 제외합니다.

**저장소 기본** 설정은 대표 워크트리의 파일에서 후보를 찾으며 모든 워크트리가
상속합니다. **이 워크트리**에서는 해당 워크트리의 파일에서 후보를 찾고 실행과
테스트를 각각 덮어쓸 수 있습니다. **기본값 복사해 변경**으로 시작하거나 후보를
선택할 수 있으며 **Override 해제**로 다시 기본값을 상속합니다. 저장한 명령의
원본 후보가 사라져도 수정값은 유지되고 화면에 표시됩니다. 설정은 저장소 파일이
아닌 `%LOCALAPPDATA%\Nebb\DevManager\command-settings`에 저장됩니다.
서비스 목록도 저장소 기본값을 상속하며, 워크트리에서 기본 목록을 복사해 서비스를
추가·제거·수정하거나 서비스 Override를 해제할 수 있습니다.
일반 저장소에서 Run 명령을 저장하면 해당 워크트리의 **실행/전환**이 활성화됩니다.
이를 누르면 같은 저장소에서 Dev Manager가 시작한 이전 Run 명령을 종료하고 선택한
워크트리의 Run 명령을 시작합니다. 명령과 인수는 선택한 워크트리의 작업 폴더에서
지정한 환경변수로 실행됩니다. **종료**는 Dev Manager에서 시작한 명령만 종료하며,
상태와 최근 로그를 화면에서 확인할 수 있습니다. 실행 기록과 로그는
`%LOCALAPPDATA%\Nebb\DevManager\command-runs`에 보관합니다. 테스트·서비스 명령은
계속 설정만 가능하며, 명령 설정 화면에서는 명령을 실행하지 않습니다. 일반 저장소의
Run 명령은 Dev Manager를 닫을 때 함께 종료됩니다.

PixPeek 서버 실행에는 Node.js/npm과 Chrome이 필요합니다. 기존 PixPeek Dev
Manager와 같은 단일 실행 잠금 및 `%LOCALAPPDATA%\PixPeek\DevManager`의 실행
기록을 사용하므로, 새 앱을 사용하려면 기존 앱을 닫으세요.

## nebb 로컬 모델 준비

**로컬 모델**에서 Qwen3 1.7B 또는 4B의 Q4_K_M GGUF 파일을 선택해 설치할 수
있습니다. PC가 16 GB급 물리 메모리와 논리 프로세서 4개 이상이면 4B를,
그 외에는 1.7B를 추천합니다. 추천과 무관하게 사용자가 직접 선택할 수 있습니다.

설치 파일은 Qwen의 Hugging Face 저장소에서 내려받아 크기와 SHA-256을 확인합니다.
설치 위치는 `%LOCALAPPDATA%\Nebb\models`입니다. 다운로드를
취소하거나 검증에 실패하면 임시 파일을 지웁니다. 이미 설치한 모델은 다시
다운로드하지 않고 선택할 수 있으며, 두 모델을 모두 보관할 수 있습니다.
현재 선택한 모델은 같은 폴더의 `selection.json`에 기록됩니다. 이후 nebb의
모델 사용 기능은 `LocalModelManager.GetSelectedModel()`에서 모델 경로를
가져올 수 있습니다. 이번 단계에는 모델 실행과 대화 화면이 포함되지 않습니다.

1.7B Q4_K_M은 [Qwen 공식 저장소의 고정된 이전 리비전](https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/tree/7fb011e9aee6e4dc7adf8430df9ea8de6a466aa3),
4B Q4_K_M은 [Qwen 공식 저장소](https://huggingface.co/Qwen/Qwen3-4B-GGUF)의
고정된 리비전을 사용합니다.
