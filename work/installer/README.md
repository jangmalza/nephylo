# Windows 설치 프로그램

`Nephylo.iss`는 Inno Setup 7.1.0으로 사용자별 Windows 설치 파일 `Nephylo-Setup.exe`를 만듭니다. 한국어·영어, 시작 메뉴, 앱 제거 등록, 선택적 자동 실행·바탕 화면 바로가기와 설치 후 실행을 지원합니다. 릴리스의 전체 순서는 [RELEASING.md](../../docs/RELEASING.md)를 따릅니다.

## 컴파일러와 입력 파일

컴파일러의 공식 배포본 전체를 `work/tools/innosetup-7.1.0/`에 준비합니다. `ISCC.exe`, 기본 언어 파일, `Languages/Korean.isl`이 필요합니다. 도구 사용은 해당 배포본의 라이선스 안내를 따릅니다. 이 프로젝트의 사용자 설치 파일이나 Git에 도구를 포함하지 않습니다.

`build-setup.ps1`은 `-Compiler` → `INNO_SETUP_COMPILER` → 위 기본 경로 순서로 컴파일러를 선택합니다. 현재 두 설치 회귀 스크립트는 기본 경로를 직접 사용하므로 테스트하려면 그 위치도 준비해야 합니다. 새 clone에 필요한 고지문 준비 방법은 [새 PC 준비](../../docs/RELEASING.md#1-새-pc에서-준비할-것)에 있습니다.

| 컴파일 정의 | 의미 |
| --- | --- |
| `SourceDir` | 아래 6개 payload 파일이 있는 절대 경로 |
| `OutputDir` | 설치 파일을 쓸 절대 경로 |
| `AppVersion` | 실제 세 자리 릴리스 버전; 직접 생략하면 소스 기본값 1.19.0이므로 반드시 명시 |

payload는 `UsageWidget.exe`, `UsageWidget.exe.config`, `widget.ico`, `widget.png`, `README.txt`, `THIRD-PARTY-NOTICES.txt`입니다. 재귀 와일드카드나 사용자 설정은 포함하지 않습니다. 공개 portable ZIP에는 추가로 `Install.cmd`가 있으며 설치 EXE의 payload에는 필요하지 않습니다.

`build-setup.ps1`이 payload 존재 여부와 실행 파일 버전 `<AppVersion>.0`의 일치를 검사합니다. 이 스크립트 자체가 앱을 빌드하지는 않습니다. 최종 공개 설치 파일은 `package-release.ps1`이 생성·서명하므로 이미 만들어 둔 immutable 후보를 다시 컴파일하지 않습니다.

## OS와 사용자 데이터

설치 기준은 Windows 10 1903(빌드 18362) 이상 x64, Windows 11 x64/ARM64의 x64 호환 환경입니다. 32비트 Windows와 Windows 10 ARM64는 제외합니다. .NET Framework 4.8을 사용하며 외부 서비스 CLI에는 별도 요구사항이 있습니다. Windows 11의 둥근 모서리·다크 제목 표시줄은 선택 기능입니다.

기존 설치 회귀 검사는 Windows 11 x64에서 수행했습니다. 사용자가 과거 1.18.1의 Windows 10 설치 성공을 확인한 기록은 이후 버전의 전체 기능·ARM64 실기기 검증을 대신하지 않습니다. 매 릴리스의 실기기 검증 범위는 별도로 기록합니다.

설치 경로는 `%LOCALAPPDATA%\UsageWidget\App`으로 고정하며 `/DIR` 변경을 거부합니다. 관리자 권한 상승이나 상위 사용자 데이터 폴더 이동은 하지 않습니다. 설정·API 키·로그인·사용 이력과 알 수 없는 기존 파일은 설치·제거 시 보존합니다.

실행 중인 앱은 mutex로 설치·제거를 막습니다. 설치 프로그램은 프로세스를 강제로 닫거나 재부팅 시 파일 교체를 예약하지 않습니다. 이전 버전으로의 다운그레이드와 설치 경로의 폴더 링크를 거부합니다. ZIP 설치와 Setup은 같은 설치 mutex를 사용합니다.

Windows 자동 실행은 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`의 `UsageWidget` 값을 사용합니다. 현재 값을 읽어 선택을 복원하며 `/TASKS`, `/MERGETASKS`로 명시할 수 있습니다. 다른 프로그램·사용자가 수정한 명령은 보존하고, 제거 시에는 고정 설치 경로의 실행 파일을 정확히 가리키는 값만 삭제합니다.

시작 메뉴·바탕 화면 바로가기도 대상 경로와 인수를 확인하여 소유한 것만 생성·갱신·제거합니다. 과거 `Milos`, `Usage Widget` 바로가기는 같은 실행 파일과 빈 인수를 가리킬 때만 정리합니다. portable 경로에서 등록한 Claude Code 연결은 설치 후 서비스 관리에서 복구할 수 있으며 Setup이 별도 Claude 설정을 수정하지 않습니다.

## 격리 설치 테스트

저장소 루트에서 실제 테스트할 버전을 명시합니다. 새 버전의 앱 빌드를 `work/release-<버전>/`에 만든 뒤 실행합니다. 아래는 기존 로컬 1.25.3 payload를 검사하는 예시이며 최종 설치 파일을 재패키징하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File work/tests/setup-tests.ps1 -Version 1.25.3
powershell -NoProfile -ExecutionPolicy Bypass -File work/tests/auto-update-installation-flow-tests.ps1 -ReleaseAssets work/release-1.25.3
```

각 명령의 종료 코드가 0인지 확인합니다. 첫 스크립트의 기본 버전은 1.18.1, 두 번째의 기본 입력은 `release-1.20.0`이므로 기본값으로 실행하지 않습니다. 공개 준비 시 추가 검사·실패 시 중단 처리는 [릴리스 절차](../../docs/RELEASING.md)에 있습니다.

테스트 설치기는 컴파일 시 `TestRoot`와 짧은 고유 `TestId`를 **함께** 지정합니다. 공개 설치 파일에는 실행 시 변경 가능한 테스트 경로가 없습니다.

| 대상 | 테스트 위치 |
| --- | --- |
| 앱 | `TestRoot\data\App` |
| 보존할 데이터 | `TestRoot\data` |
| 바로가기 | `TestRoot\shortcuts\start-menu`, `TestRoot\shortcuts\desktop` |
| 자동 실행 | `HKCU\Software\UsageWidget\Tests\Setup\TestId\Run` |
| 제거 등록 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Nephylo.SetupTest.TestId_is1` |

테스트 설치기는 자체 mutex를 사용하며 실제 앱 실행·운영 mutex 검사를 생략합니다. `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`로 신규 설치·같은 버전 재설치·제거, 사용자 데이터 해시 보존, 자동 실행·바로가기 소유권, 다운그레이드와 `/DIR` 거부를 확인합니다. 격리 검사는 공개 설치 파일을 실사용 환경에서 설치한 결과와 구분합니다.

## 참고

- [Inno Setup AppMutex](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm)
- [설치 이벤트](https://jrsoftware.org/ishelp/topic_scriptevents.htm)
- [지원 아키텍처](https://jrsoftware.org/ishelp/topic_archidentifiers.htm)
- [.NET Framework 요구사항](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)
