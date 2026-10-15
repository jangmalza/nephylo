# 라이선스와 외부 구성요소

Nephylo 자체 코드·문서·자체 제작 아이콘에는 루트 [MIT License](../LICENSE)를 적용합니다. 이미 별도 라이선스가 명시된 외부 자료는 해당 조건을 유지합니다. MIT는 저작권과 허가 고지 보존을 조건으로 상업적 이용과 수정·재배포를 허용하며, 수정본 소스 공개를 강제하지 않습니다.

| 구성요소 | 조건 | 포함 방식 |
| --- | --- | --- |
| Nephylo 코드·문서·자체 아이콘 | MIT, Copyright 2026 Nephylo contributors | 소스 포함 |
| Codex·ElevenLabs·OpenAI·Claude·Gemini·Cursor 아이콘 | MIT, Copyright 2023 LobeHub | 고정 원본 SVG와 WPF 변환, 원문 고지 유지 |
| Higgsfield 항목의 H 도형 | 프로젝트 자체 제작, MIT | 공식 브랜드 로고가 아닌 구분용 도형 |
| PGlite 0.3.14 | Apache-2.0 | 개발용 테스트 의존성. npm으로 설치, 앱/소스 묶음에 node_modules 미포함 |
| Inno Setup 7.1.0 | Inno Setup License | 설치기 생성 도구, 컴파일러 배포본은 저장소 미포함 |
| .NET Framework·Windows 글꼴 | OS 제공 구성요소 | 시스템 참조, 글꼴 파일 재배포 없음 |

아이콘 원본·저작권·MIT 전문은 [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) 및 `work/assets/service-icons/`에 있습니다. 고정 원본은 LobeHub commit `c385b2b8d1f9e19aa86e628d4e23c91ee1111a47`입니다.

서비스 이름·상표의 소유권은 각 제공자에게 있습니다. 포크나 수정본을 Nephylo 또는 AI 제공자의 공식 제품으로 오인시키지 마세요. 이 안내는 MIT 코드 이용 권한에 별도 제한을 추가하지 않습니다.

향후 새 설치 파일·ZIP은 `work/compose-notices.ps1`로 프로젝트 MIT 전문과 외부 고지를 한 파일에 결합해 기존 설치 payload에 넣습니다. 기존 공개 버전의 불변 EXE·ZIP은 재생성하지 않습니다.

근거: [MIT](https://choosealicense.com/licenses/mit/), [고정 LobeHub 라이선스](https://github.com/lobehub/lobe-icons/blob/c385b2b8d1f9e19aa86e628d4e23c91ee1111a47/LICENSE), [Inno Setup 라이선스](https://jrsoftware.org/files/is/license.txt), [Inno Setup 구매 FAQ](https://jrsoftware.org/isorder.php).
