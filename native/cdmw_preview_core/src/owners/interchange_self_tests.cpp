static void run_interchange_decode_self_test() {
    const auto require = [](bool value, const char* message) {
        if (!value) throw std::runtime_error(message);
    };
    const auto put32 = [](std::vector<char>& data, size_t at, std::uint32_t value) {
        for (int i = 0; i < 4; ++i) data[at + i] = static_cast<char>(value >> (i * 8));
    };
    const auto put16 = [](std::vector<char>& data, size_t at, std::uint16_t value) {
        data[at] = static_cast<char>(value); data[at + 1] = static_cast<char>(value >> 8);
    };
    std::vector<char> record(40, 0);
    const std::uint32_t normal = 123u | (512u << 10) | (512u << 20) | (1u << 30);
    put32(record, 16, normal);
    const auto decoded = decode_pac_normal_exact(record, 0, 16);
    require(decoded.z < -0.999 && std::abs(decoded.x * decoded.x + decoded.y * decoded.y + decoded.z * decoded.z - 1.0) < 1e-12,
        "PAC normal did not reconstruct signed z on the unit sphere");
    put32(record, 16, normal ^ 0x800003ffu);
    const auto changed_tangent = decode_pac_normal_exact(record, 0, 16);
    require(decoded.x == changed_tangent.x && decoded.y == changed_tangent.y && decoded.z == changed_tangent.z,
        "PAC tangent bits changed an exported normal");
    put32(record, 20, 1u | (2u << 10) | (3u << 20));
    put32(record, 24, 4u | (5u << 10) | (6u << 20));
    put16(record, 12, 0x4700); // half 7
    put16(record, 14, 0x4800); // half 8
    for (int i = 0; i < 8; ++i) record[28 + i] = static_cast<char>(i < 6 ? 50 : 100);
    const auto skin = decode_pac_skin(record, 0, 40);
    for (int i = 0; i < 8; ++i) require(skin.slots[i] == i + 1 && skin.weights[i] == (i < 6 ? 50 : 100), "PAC lost a skin influence");
    record[39] = 63;
    const auto gated = decode_pac_skin(record, 0, 40);
    require(gated.weights[6] == 0 && gated.weights[7] == 0, "PAC ignored the extra-influence gate");
    record[39] = 0;
    put16(record, 12, 0x7e00); // half NaN
    require(decode_pac_skin(record, 0, 40).weights[6] == 0, "PAC accepted a nonfinite bone slot");

    const size_t geometry = 0x50 + 2 * 0x210;
    std::vector<char> pamlod(geometry + 48, 0);
    put32(pamlod, 0, 1); put32(pamlod, 4, static_cast<std::uint32_t>(geometry));
    for (size_t i = 0; i < 3; ++i) put32(pamlod, 0x1c + i * 4, 0x3f800000);
    const std::string names[] = {"stone", "wood"};
    for (size_t part = 0; part < 2; ++part) {
        const size_t at = 0x50 + part * 0x210;
        put32(pamlod, at, 3); put32(pamlod, at + 4, 3);
        put32(pamlod, at + 8, static_cast<std::uint32_t>(part * 3));
        put32(pamlod, at + 12, static_cast<std::uint32_t>(part * 3));
        const std::string texture = names[part] + ".dds";
        std::copy(texture.begin(), texture.end(), pamlod.begin() + at + 16);
        std::copy(names[part].begin(), names[part].end(), pamlod.begin() + at + 0x110);
        put16(pamlod, geometry + part * 18 + 6, 65535);
        put16(pamlod, geometry + part * 18 + 14, 65535);
    }
    const std::uint16_t faces[] = {0, 1, 2, 2, 1, 0};
    for (size_t i = 0; i < 6; ++i) put16(pamlod, geometry + 36 + i * 2, faces[i]);
    const auto parts = parse_pamlod_submeshes(pamlod);
    require(parts.meshes.size() == 2, "PAMLOD merged material parts");
    for (size_t part = 0; part < 2; ++part) {
        const auto& mesh = parts.meshes[part];
        require(mesh.material == names[part] && mesh.texture == names[part] + ".dds", "PAMLOD lost material identity");
        require(mesh.positions.size() == 3 && mesh.indices.size() == 3, "PAMLOD changed part geometry");
        for (size_t corner = 0; corner < 3; ++corner) require(mesh.indices[corner] == faces[part * 3 + corner], "PAMLOD offset local face indices");
    }

    const unsigned char compressed[] = {0x4f, 'A', 'B', 'C', 'D', 4, 0, 0xe4, 0x50, 'D', 'A', 'B', 'C', 'D'};
    std::vector<char> pam(0x50, 0);
    std::copy_n("PAR ", 4, pam.begin());
    put32(pam, 4, 0x1802); put32(pam, 0x3c, 0x50);
    put32(pam, 0x40, 256); put32(pam, 0x44, sizeof(compressed));
    pam.insert(pam.end(), std::begin(compressed), std::end(compressed));
    pam.insert(pam.end(), {'t', 'a', 'i', 'l'});
    ArchiveEntryRef entry;
    entry.extension = ".pam"; entry.flags = 1;
    entry.comp_size = pam.size(); entry.orig_size = 0x50 + 256 + 4;
    const auto unpacked = decode_archive_ref_bytes(entry, pam);
    require(unpacked.size() == entry.orig_size && read_u32(unpacked, 0x44) == 0, "Partial PAM was not decompressed");
    require(std::string(unpacked.end() - 4, unpacked.end()) == "tail", "Partial PAM lost its trailer");
    for (size_t i = 0; i < 256; ++i) require(unpacked[0x50 + i] == "ABCD"[i % 4], "Partial PAM changed geometry bytes");

    put32(pam, 4, 0x01001806);
    require(decode_archive_ref_bytes(entry, pam).size() == entry.orig_size, "Current PAM version was not decompressed");
    std::vector<char> lod(0x60, 0);
    put32(lod, 0, 1); put32(lod, 4, 0x60);
    put32(lod, 0x50, 0x60); put32(lod, 0x54, 256); put32(lod, 0x58, sizeof(compressed));
    lod.insert(lod.end(), std::begin(compressed), std::end(compressed));
    entry.extension = ".pamlod"; entry.flags = 0x101;
    entry.comp_size = lod.size(); entry.orig_size = 0x60 + 256;
    const auto expanded_lod = decode_archive_ref_bytes(entry, lod);
    require(expanded_lod.size() == entry.orig_size && read_u32(expanded_lod, 0x58) == 0, "Partial PAMLOD was not decompressed");
    for (size_t i = 0; i < 256; ++i) require(expanded_lod[0x60 + i] == "ABCD"[i % 4], "Partial PAMLOD changed geometry bytes");
    put32(lod, 0x50, 0x61);
    bool rejected = false;
    try { decode_archive_ref_bytes(entry, lod); } catch (const std::exception&) { rejected = true; }
    require(rejected, "Invalid PAMLOD block offset was accepted");

    const auto descriptors = skeleton_descriptor_paths("character/model/1_pc/8_pdw/head/head/cd_pdw_00_head_00_0005.pac");
    require(std::find(descriptors.begin(), descriptors.end(), "character/prefab/1_pc/08_pdw/nude/cd_pdw_00_nude_00_0001.prefabdata_xml") != descriptors.end(),
        "Head preview lost its normalized family rig descriptor");
    const auto creature = skeleton_descriptor_paths("character/model/2_mon/stone/stone_0002.pac");
    require(std::find(creature.begin(), creature.end(), "character/prefab/2_mon/stone/stone_nude_0002.prefabdata_xml") != creature.end(),
        "Creature preview lost its exact nude variant descriptor");
    const std::string descriptor = "<BodyPrefabData><SkeletonName FileName=\"1_pc/2_phw/phw_01.pab\"/><SkeletonVariationName FileName=\"unrelated/body.pabc\"/></BodyPrefabData>";
    const auto rigs = declared_skeleton_paths(std::vector<char>(descriptor.begin(), descriptor.end()));
    require(rigs == std::vector<std::string>{"character/model/1_pc/2_phw/phw_01.pab"}, "Family body supplied unrelated appearance inputs");
}
