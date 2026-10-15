# Nephylo

[Website](https://nephylo.com/) · [Source](https://github.com/jangmalza/nephylo) · [MIT License](LICENSE)

A free, open-source Windows widget for AI service usage, remaining quotas and reset times. Built with C# / WPF / .NET Framework 4.8. Current source version: **{{SOURCE_VERSION}}**. No Nephylo account or subscription is required; provider authentication and fees are separate.

Public source updates are automatically synchronized from a checked snapshot. Private deployment records, account settings and Git history are excluded.

여러 AI 서비스의 잔여량과 초기화 시각을 모아 보는 Windows 위젯입니다. 일반 위젯, 미니바, 대시보드, 알림, 최대 30일 이력과 CSV 내보내기를 제공합니다. Nephylo 자체 로그인이나 결제는 필요하지 않으며 AI 서비스의 인증·구독·API 요금은 별도입니다.

## 라이선스

Nephylo 소스는 [MIT License](LICENSE)로 제공합니다. 수정·재배포·상업적 이용이 가능하며 저작권 및 라이선스 고지를 보존해야 합니다. 외부 서비스 아이콘 등의 조건은 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)와 [라이선스 목록](docs/LICENSES.md)을 확인하세요. 코드 공개가 서비스 상표나 외부 API 사용 권한을 대신하지는 않습니다.

## 빌드와 미리보기

Windows 10 1903 이상 x64 또는 Windows 11, .NET Framework 4.8과 Windows PowerShell 5.1을 사용합니다.
테스트용 임시 경로가 길어질 수 있으므로 `C:\src\nephylo`처럼 짧은 경로에서 작업하세요. 오래된 .NET Framework 경로 처리에는 길이 제한이 있습니다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\work\build.ps1
if ($LASTEXITCODE -ne 0) { throw '빌드 실패' }
.\work\build\UsageWidget.exe --demo
```

`--demo`는 예시 데이터로 화면을 확인합니다. 일반 실행은 해당 Windows 사용자의 로컬 프로필을 사용하므로 개발·실계정 테스트를 구분하세요. 기본 소스 빌드는 설치된 앱 경로가 아니므로 자동 설치 대상이 아닙니다.

## 소스 배포 범위

이 소스 묶음에는 Git 과거 이력, 운영 배포 스크립트, 실계정 검증 기록, 비밀 키, 기존 EXE·ZIP을 포함하지 않습니다. 웹 관리자 설정은 비활성 예제입니다. 공식 업데이트 검증용 **공개키**와 서명 검사에 쓰는 공개 메타데이터는 포함되며 개인키는 포함되지 않습니다.

웹사이트는 미리보기할 수 있지만 로컬 다운로드 버튼을 서비스하려면 자신의 릴리스 산출물이 필요합니다. 공식 사용자는 [nephylo.com](https://nephylo.com/)에서 설치할 수 있습니다.

## 테스트

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\work\tests\run-ci-tests.ps1
```

공개용 CI는 Windows 빌드·48개 오프라인 스위트, 서버 격리 테스트, 비밀정보 검사, 소스 구성·JavaScript 문법을 확인합니다. 운영 서버 배포나 실제 사용자 인증은 하지 않습니다.

포크 설정·독립 배포 방법은 [오픈소스 안내](docs/OPEN-SOURCE.md), 기여 방법은 [CONTRIBUTING.md](CONTRIBUTING.md)를 참고하세요.
