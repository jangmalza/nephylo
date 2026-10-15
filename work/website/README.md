# 사이트 개발 안내

HTML·CSS·JavaScript 정적 사이트 소스는 `dist/`에 있습니다. 저장소 루트에서 다음 명령으로 미리보기합니다.

```powershell
node work/website/preview.mjs
```

주소는 `http://127.0.0.1:4173/`입니다. 별도 번들 빌드나 위젯 로그인이 필요하지 않습니다.

공개 소스에는 EXE·ZIP과 운영 서버 설정이 포함되지 않습니다. 다운로드 버튼을 직접 서비스하려면 자신의 릴리스 산출물·서명·주소를 함께 준비해야 합니다. `work/website/check.mjs`는 완성된 배포 파일 검사이므로 파일이 없는 공개 소스에서 그대로 실행하면 실패합니다. 공개 소스 CI는 `node scripts/check-public-source.mjs`를 사용합니다.

관리자 연결 설정은 비활성 예제로 제공합니다. 필요한 경우 [기존 서버 코드 안내](../membership/README.md)와 루트 [오픈소스 안내](../../docs/OPEN-SOURCE.md)를 참고해 자신의 프로젝트를 설정하세요. 공식 사이트는 [nephylo.com](https://nephylo.com/)입니다.
