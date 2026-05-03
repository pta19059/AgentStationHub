set -e
echo "=== manifest.json (search) ==="
docker exec asb-3b35dfc80a3e bash -lc 'find /workspace -maxdepth 5 -name manifest.json 2>/dev/null'
echo
echo "=== /workspace/manifest.json ==="
docker exec asb-3b35dfc80a3e bash -lc 'cat /workspace/manifest.json 2>/dev/null || echo "(not at /workspace/manifest.json)"'
echo
echo "=== /workspace/infra/main.parameters.json ==="
docker exec asb-3b35dfc80a3e bash -lc 'cat /workspace/infra/main.parameters.json 2>/dev/null || find /workspace -maxdepth 5 -name main.parameters.json 2>/dev/null'
echo
echo "=== azure.yaml (head 80) ==="
docker exec asb-3b35dfc80a3e bash -lc 'cat /workspace/azure.yaml 2>/dev/null | head -80 || echo no-azure-yaml'
echo
echo "=== azd env get-values ==="
docker exec asb-3b35dfc80a3e bash -lc 'cd /workspace && azd env get-values 2>&1 | head -40 || true'
echo
echo "=== /workspace ls ==="
docker exec asb-3b35dfc80a3e bash -lc 'ls -la /workspace | head -40'
