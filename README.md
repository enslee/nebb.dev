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
동작합니다. 다른 저장소에서는 워크트리를 선택한 뒤 **프로필 설정**에서 Run Profile을
만듭니다. 프로필에는 실행 후보나 직접 입력한 Process·Shell script를 여러 개 추가할 수
있습니다. Tauri 근거가 있으면 **Tauri App**과 **Frontend Only** 후보를 구분해 표시하며
후보는 자동 추가하지 않습니다. **테스트·서비스 설정**은 별도 화면에서 유지하며 Docker
Compose 서비스는 여러 개를 선택해 각각 수정·저장할 수 있습니다.
선택한 워크트리 안에서 다른 `.git` 저장소나 워크트리를 만나면 그 내부는 탐색하지
않으며 `.claude/worktrees`의 복사본도 제외합니다.

**저장소 기본** 프로필 목록은 모든 워크트리가 상속합니다. **이 워크트리**에서 기본
목록을 복사하면 분리된 snapshot Override가 되어 이후 기본 목록 변경이 반영되지
않습니다. Override 해제로 다시 상속할 수 있습니다. 기존 단일 Run 명령은 항목 하나인
프로필로 읽고 새 형식으로 처음 저장할 때 원본 설정을 백업합니다. 설정은 저장소 파일이
아닌 `%LOCALAPPDATA%\Nebb\DevManager\command-settings`에 저장됩니다.
테스트·서비스 명령의 기존 상속 및 Override는 유지됩니다. 저장한 후보의 원본이
사라져도 설정값은 유지됩니다.
Shell script는 인라인 입력 또는 `.ps1`·`.bat`·`.cmd` 파일을 사용할 수 있습니다.
인라인의 기본 셸은 PowerShell이며 파일은 확장자로 셸을 선택합니다. 워크트리 밖의
작업 폴더나 스크립트 파일은 경고를 확인한 뒤 저장할 수 있습니다.
서비스 목록도 저장소 기본값을 상속하며, 워크트리에서 기본 목록을 복사해 서비스를
추가·제거·수정하거나 서비스 Override를 해제할 수 있습니다.
선택한 워크트리에서 프로필을 고르고 **Run All**을 누르면 활성 항목을 독립 Windows Job
Object로 실행합니다. 의존 항목은 선행 Step 완료 또는 프로세스·포트 준비 확인 후
시작하고, 독립 항목은 병렬 실행합니다. 의존성은 시작 순서에만 적용됩니다. 각 항목의
상태·stdout·stderr를 따로 확인하고 Start·Restart·Stop할 수 있습니다. 실행 중인
프로필에는 **Restart All**과 **Stop All**이 표시됩니다. 다른 프로필·워크트리는 동시에
실행할 수 있으며, 알려진 포트 충돌은 실행 전에 경고합니다. 포트 준비는 기본적으로
해당 항목의 Job에 속한 프로세스가 listen할 때만 완료되고, Docker처럼 외부 프로세스가
포트를 여는 항목은 **외부 포트 인정**을 선택할 수 있습니다. 실행 기록과 로그는
`%LOCALAPPDATA%\Nebb\DevManager\profile-runs`에 보관하고 앱 종료 시 실행 중인 Job의
프로세스 트리를 종료합니다. 테스트·서비스 명령은 계속 설정만 가능합니다.

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
