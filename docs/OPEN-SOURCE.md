# 오픈소스 공개와 포크 안내

이 분기의 공개 소스 후보는 위젯 제품만 포함합니다. ADE 소스·검사·개발 실행기·이전 검증 기록은 `other-product-source`로 제외하고 공개 후보 검사에서도 혼입을 거부합니다. AI 개발 환경은 별도 공개 저장소 [Nephylo ADE](https://github.com/jangmalza/nephylo-ade)에서 개발합니다. 위젯 저장소 이름 변경과 동기화 대상 전환은 별도 진행합니다.

공식 공개 소스: [jangmalza/nephylo](https://github.com/jangmalza/nephylo), `v1.25.21` 태그. 2026-10-15 운영 저장소의 이력을 가져오지 않은 새 저장소로 공개했으며, 공개용 Source CI의 Windows·서버 격리·소스 구성·비밀정보 검사가 통과했습니다. 아래 절차는 후속 공개 스냅샷을 준비할 때도 사용합니다.

## MIT 적용 범위

Nephylo 소스는 MIT입니다. 다른 사람이 유료 제품이나 비공개 수정본에 사용할 수 있으며 저작권·라이선스 고지를 유지해야 합니다. 외부 구성요소는 [라이선스 목록](LICENSES.md)의 원래 조건을 따릅니다.

## 공개용 소스 준비

운영 저장소에서 아래 명령으로 **새 디렉터리**에 공개용 스냅샷을 만듭니다. 기존 폴더를 덮어쓰거나 Git 이력을 변경하지 않습니다.

```powershell
node scripts/prepare-open-source.mjs outputs/open-source/nephylo-source
node scripts/check-public-source.mjs outputs/open-source/nephylo-source
```

이력·개인환경이 담긴 검증 기록, 운영 서버 자료, 기존 실행 파일·ZIP, 로컬 백업은 제외합니다. 과거 회원·사이트 배포 문서와 공식 운영 패키징 도우미도 제외하며 필요한 개발 문서는 `docs/public/`의 일반 안내로 교체합니다. 관리자 `account/config.json`은 비어 있는 예제로 바꾸며 공개용 CI와 README를 사용합니다. 파일 목록·해시는 Git 텍스트 정규화 전의 내보낸 파일 기준으로 `source-manifest.json`에 기록합니다. 입력은 Git 추적 파일과 ignore되지 않은 새 소스의 명시적 허용 목록뿐이며 경로 링크는 거부합니다.

원래 저장소를 곧바로 Public으로 바꾸면 **이전 커밋의 운영값·개인환경 기록도 함께 보입니다**. 현재 파일 정리나 새 스냅샷 생성은 과거 이력을 지우지 않습니다. 과거 이력을 보존할지, 정리한 스냅샷으로 새 공개 저장소를 시작할지는 공개 전에 결정하세요. 이 도구는 원격 저장소 생성·공개 전환·강제 푸시를 수행하지 않습니다.

`outputs/` 또는 작업 폴더 전체를 압축해 공개하지 마세요. 실제 서비스 키와 로그인 상태의 백업이 있을 수 있습니다. 공개용 스냅샷의 원본 출처 SHA는 provenance이며, 과거 Git 객체를 포함하지 않습니다.

## 관리자 설정

위젯은 Nephylo 회원 서버 없이 작동합니다. 웹 관리자 기능이 필요할 때만 `work/website/account-config.example.json`을 로컬 `account-config.local.json`으로 복사하고 자신의 Supabase 프로젝트 URL·publishable 키·활성 제공자를 설정하세요. secret/service-role 키는 넣지 않습니다.

운영 사이트의 실제 공개 설정은 별도 배포 입력입니다. 공개용 스냅샷은 항상 비활성 예제를 사용합니다. 운영 자료의 공개키를 복사해 다른 서비스의 관리자 백엔드로 사용하지 마세요. 개인정보처리 안내·이메일·도메인도 자신의 운영 내용으로 바꿉니다.

배포할 로컬 사본에서 `node scripts/configure-account.mjs work/website/account-config.local.json`을 실행하면 네 개의 공개 필드만 검증해 `dist/account/config.json`에 씁니다. secret 키나 추가 필드는 거부합니다. 생성된 운영 설정을 공개용 소스에 커밋하지 말고, 소스 스냅샷은 예제 설정 상태로 검사하세요. 이 작업은 운영 서버에 자동 배포하지 않습니다.

## 포크의 독립 배포

기본 `work/build.ps1` 결과는 portable 개발 실행 파일이며 `--demo`로 먼저 확인합니다. 공식 설치 위치에서 실행되는 앱만 자동 설치 대상입니다. 별도 제품으로 배포하려면 다음 항목을 함께 변경·검증하세요.

- `AutoUpdateClient.DownloadBase`와 서명된 manifest URL: 자신의 HTTPS 배포 주소.
- `work/assets/update-signing-public.xml`: 자신의 검증 공개키. 공식 개인키를 요청하거나 배포하지 않습니다.
- `sign-release.ps1`의 `-PublicKeyPath`·`-PrivateKeyPath`: 개인키는 소스 폴더 밖에서 보호합니다. 기존 공식 키를 덮어쓰지 마세요.
- 설치기의 `SetupAppId`, 설치 디렉터리, mutex, 바로가기·자동 실행 식별자: 공식 Nephylo와 충돌하지 않게 분리합니다.
- PowerShell 설치 스크립트의 도메인·검증 공개키와 웹 다운로드 안내도 같은 배포로 맞춥니다.

업데이트 서명 검증을 끄거나 개인키를 저장소에 넣어 빌드를 통과시키면 안 됩니다. 공식 앱을 설치한 사용자에게 자신의 포크를 같은 버전·같은 설치 경로로 배포하지 마세요. 공개 EXE·ZIP의 기존 버전은 덮어쓰지 않습니다.

## 비밀정보 검사

CI는 체크섬으로 고정한 Gitleaks 8.30.1로 전체 Git 이력과 현재 파일 스냅샷을 검사합니다. 공개 publishable 키와 확인한 **정확한 테스트 값 + 해당 경로**만 예외로 둡니다. 테스트 디렉터리나 generic-api-key 규칙 전체를 제외하지 않습니다. 실패·검사기 실행 오류·누락된 작업은 CI와 CD 승인을 차단합니다.

로컬에서는 검증한 검사기를 `GITLEAKS_BINARY`에 지정한 뒤 `node --test .github/scripts/secret-policy.test.mjs`와 `node .github/scripts/scan-secrets.mjs`를 실행합니다. 업로드되는 결과는 경로·규칙·위치뿐이며 토큰 본문을 포함하지 않습니다. 자동 검사만으로 모든 비밀정보 형식이나 제3자 권리를 보증할 수는 없습니다.
